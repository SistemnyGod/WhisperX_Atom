from __future__ import annotations

import asyncio
import json
import logging
import os
from pathlib import Path, PurePosixPath
from typing import Any

from workers.nats_utils import fetch_available, maintain_message
from whisperx_atom.contracts import ProcessingRequest
from whisperx_atom.processing import ProcessingService
from workers.gpu_lease import PostgresGpuLease
from workers.runtime_heartbeat import AsyncHeartbeat
from .persistence import JobRepository

class ResidentLlmConflict(RuntimeError):
    pass


async def resident_llm_detected() -> bool:
    if os.getenv("GPU_ENFORCE_NO_RESIDENT_LLM", "true").lower() not in {"1", "true", "yes"}:
        return False
    try:
        _, writer = await asyncio.wait_for(asyncio.open_connection(os.getenv("LLM_HEALTH_HOST", "llama-server"), int(os.getenv("LLM_HEALTH_PORT", "8080"))), timeout=1.0)
        writer.close()
        await writer.wait_closed()
        return True
    except (OSError, asyncio.TimeoutError):
        return False
LOGGER = logging.getLogger("whisperx.gpu-worker")


def resolve_storage_path(storage_key: str) -> Path:
    """Translate Docker `/data/...` keys when the GPU worker runs on Windows."""
    value = str(storage_key or "").strip()
    host_root = os.getenv("WHISPERX_DATA_HOST", "").strip()
    if os.name == "nt" and host_root:
        posix_path = PurePosixPath(value.replace("\\", "/"))
        try:
            relative = posix_path.relative_to("/data")
        except ValueError:
            pass
        else:
            return Path(host_root).joinpath(*relative.parts)
    return Path(value)


def error_code_for(exc: Exception) -> str:
    text = f"{type(exc).__name__}: {exc}".lower()
    if "transcript_empty" in text:
        return "TRANSCRIPT_EMPTY"
    if "invalid_timecode" in text:
        return "TRANSCRIPT_INVALID_TIMECODE"
    if "transcript_outside_media" in text:
        return "TRANSCRIPT_OUTSIDE_MEDIA"
    if "cuda" in text and ("out of memory" in text or "oom" in text):
        return "CUDA_OOM"
    if "cuda" in text and ("unavailable" in text or "required" in text or "not available" in text):
        return "CUDA_UNAVAILABLE"
    if isinstance(exc, FileNotFoundError):
        return "MEDIA_NOT_FOUND"
    if "hf_token" in text or "huggingface" in text or "gated" in text:
        return "MODEL_ACCESS_ERROR"
    if "no_audio" in text or "no audio" in text:
        return "MEDIA_NO_AUDIO"
    if "ffmpeg" in text or "audio" in text:
        return "AUDIO_PROCESSING_ERROR"
    return "GPU_PROCESSING_FAILED"



class GpuWorker:
    def __init__(self, heartbeat: AsyncHeartbeat | None = None) -> None:
        self._semaphore = asyncio.Semaphore(1)
        self._service = ProcessingService()
        self._repository = JobRepository()
        self._gpu_lease = PostgresGpuLease(self._repository.conninfo)
        self._heartbeat = heartbeat

    async def handle(self, message: dict[str, Any]) -> dict[str, Any] | None:
        async with self._semaphore:
            job_id = str(message["job_id"])
            if self._heartbeat:
                self._heartbeat.set_job(job_id)
                self._heartbeat.set_state("BUSY")
            LOGGER.info("received job=%s message=%s stage=%s", job_id, message.get("message_id"), message.get("stage"))
            if not self._repository.claim_message(str(message.get("message_id", "")), job_id):
                state = self._repository.job_state(job_id)
                if self._heartbeat:
                    self._heartbeat.set_job(None)
                    self._heartbeat.set_state("READY")
                if state is not None and state[0] not in ("READY", "FAILED", "CANCELLED"):
                    if self._heartbeat:
                        self._heartbeat.set_state("READY", "MESSAGE_ALREADY_CLAIMED")
                    raise RuntimeError("message_claimed_by_active_worker")
                return None
            state = self._repository.job_state(job_id)
            if state is not None and state[0] in ("READY", "FAILED", "CANCELLED"):
                LOGGER.info("skip terminal job=%s status=%s", job_id, state[0])
                if self._heartbeat:
                    self._heartbeat.set_job(None)
                    self._heartbeat.set_state("READY")
                return None
            self._repository.update_job(job_id, "RUNNING", "TRANSCRIBING", 20)
            request = ProcessingRequest(job_id=job_id, media_path=resolve_storage_path(str(message["storage_key"])), language=message.get("language", "ru"), profile=message.get("profile", "meeting"), min_speakers=int(message.get("min_speakers", 1)), max_speakers=int(message.get("max_speakers", 12)))

            def progress(stage: str, value: int) -> None:
                LOGGER.info("job=%s stage=%s progress=%s", job_id, stage, value)
                self._repository.update_job(job_id, "RUNNING", stage, value)

            failure_code: str | None = None
            try:
                if await resident_llm_detected():
                    raise ResidentLlmConflict("resident_llama_server_must_be_stopped_before_transcription")
                LOGGER.info("job=%s waiting for GPU lease path=%s", job_id, request.media_path)
                async with self._gpu_lease:
                    LOGGER.info("job=%s acquired GPU lease", job_id)
                    result = await asyncio.to_thread(self._service.process, request, progress)
                LOGGER.info("job=%s released GPU lease", job_id)
                payload = result.to_dict()
                payload["correlation_id"] = message.get("correlation_id") or payload.get("correlation_id")
                payload["meeting_id"] = str(message["meeting_id"])
                payload["processing_job_id"] = job_id
                LOGGER.info("transcript result correlation_id=%s meeting_id=%s job_id=%s", payload.get("correlation_id"), payload["meeting_id"], job_id)
                persisted = await asyncio.to_thread(self._repository.persist_result, job_id, str(message["meeting_id"]), payload)
                if not persisted:
                    LOGGER.info("job=%s result discarded because the meeting was cancelled or deleted", job_id)
                    return None
                LOGGER.info("job=%s persisted segments=%s words=%s", job_id, len(payload.get("segments", [])), len(payload.get("word_segments", [])))
                return payload
            except ResidentLlmConflict as exc:
                LOGGER.warning("job=%s waiting: %s", job_id, exc)
                failure_code = "GPU_RESIDENT_LLM_CONFLICT"
                self._repository.release_message(str(message.get("message_id", "")))
                self._repository.update_job(job_id, "QUEUED", "WAITING_FOR_GPU", 0, str(exc), "GPU_RESIDENT_LLM_CONFLICT")
                if self._heartbeat:
                    self._heartbeat.set_state("READY", "GPU_RESIDENT_LLM_CONFLICT")
                raise
            except Exception as exc:
                LOGGER.exception("job=%s failed", job_id)
                failure_code = error_code_for(exc)
                self._repository.release_message(str(message.get("message_id", "")))
                self._repository.update_job(job_id, "FAILED", "FAILED", 0, type(exc).__name__ + ": " + str(exc), failure_code)
                if self._heartbeat:
                    self._heartbeat.set_state("READY", failure_code)
                raise
            finally:
                if self._heartbeat:
                    self._heartbeat.set_job(None)
                    self._heartbeat.set_state("READY", failure_code)


async def run() -> None:
    logging.basicConfig(
        level=getattr(logging, os.getenv("LOG_LEVEL", "INFO").upper(), logging.INFO),
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )
    try:
        import nats
    except ImportError as exc:
        raise RuntimeError("Install workers/ml_worker/requirements.txt") from exc

    def gpu_capabilities() -> dict[str, Any]:
        capabilities: dict[str, Any] = {
            "cudaAvailable": False,
            "hfConfigured": bool(os.getenv("HF_TOKEN")),
            "runtime": os.getenv("GPU_WORKER_RUNTIME", "container"),
        }
        try:
            import torch

            capabilities["cudaAvailable"] = bool(torch.cuda.is_available())
            if capabilities["cudaAvailable"]:
                capabilities["cudaDevice"] = torch.cuda.get_device_name(0)
        except Exception as exc:
            capabilities["cudaError"] = type(exc).__name__
        capabilities["diarization"] = "DEGRADED" if not capabilities["hfConfigured"] else "READY"
        return capabilities

    client = await nats.connect(os.getenv("NATS_URL", "nats://nats:4222"))
    # CUDA/model capability discovery is intentionally done once at startup.
    # It must not be repeated on every heartbeat tick while ASR is running.
    startup_capabilities = {**gpu_capabilities(), "natsConnected": True}
    heartbeat = AsyncHeartbeat("gpu-worker", capabilities=lambda: dict(startup_capabilities))
    await heartbeat.start()
    heartbeat.set_state("READY")
    worker = GpuWorker(heartbeat)
    jetstream = client.jetstream()
    try:
        await jetstream.add_stream(name="WHISPERX", subjects=["media.ingest", "ml.transcribe", "llm.summarize", "llm.assistant"])
    except Exception:
        pass
    subscription = await jetstream.pull_subscribe("ml.transcribe", durable="whisperx-gpu")
    while True:
        for message in await fetch_available(subscription, nats.errors.TimeoutError):
            try:
                payload = json.loads(message.data)
                job_id = str(payload.get("job_id", ""))
                message_id = str(payload.get("message_id", ""))
                async with maintain_message(message, on_tick=lambda: asyncio.to_thread(worker._repository.renew_lease, job_id, message_id)):
                    await worker.handle(payload)
                await message.ack()
            except Exception:
                await message.nak()


if __name__ == "__main__":
    asyncio.run(run())
