from __future__ import annotations

import asyncio
import json
import logging
import os
from pathlib import Path, PurePosixPath
from typing import Any

from workers.nats_utils import ensure_stream, fetch_available, maintain_message
from whisperx_atom.contracts import ProcessingRequest
from whisperx_atom.processing import ProcessingService
from workers.gpu_lease import PostgresGpuLease
from workers.runtime_heartbeat import AsyncHeartbeat
from .persistence import JobRepository

class ResidentLlmConflict(RuntimeError):
    pass


class RetryScheduled(RuntimeError):
    def __init__(self, delay_seconds: float):
        super().__init__("GPU_JOB_RETRY_SCHEDULED")
        self.delay_seconds = delay_seconds


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
RESIDENT_LLM_RETRY_DELAY_SECONDS = max(5, int(os.getenv("GPU_RESIDENT_LLM_RETRY_DELAY_SECONDS", "15")))


def resolve_storage_path(storage_key: str) -> Path:
    """Translate a storage key below `/data` to the worker's local mount."""
    value = str(storage_key or "").strip()
    posix_path = PurePosixPath(value.replace("\\", "/"))
    try:
        relative = posix_path.relative_to("/data")
    except ValueError as exc:
        raise ValueError("invalid_storage_key") from exc
    if any(part in {"", ".", ".."} for part in relative.parts):
        raise ValueError("invalid_storage_key")
    host_root = os.getenv("WHISPERX_DATA_HOST", "").strip() if os.name == "nt" else "/data"
    return Path(host_root).joinpath(*relative.parts)


def error_code_for(exc: Exception) -> str:
    text = f"{type(exc).__name__}: {exc}".lower()
    if "asr_input_mismatch" in text:
        return "ASR_INPUT_MISMATCH"
    if "no speech" in text or "no_speech_detected" in text:
        return "NO_SPEECH_DETECTED"
    # A decodable recording that produces no segments is a valid no-speech
    # outcome. Keep the public error stable even when the quality gate raises
    # its internal TRANSCRIPT_EMPTY reason.
    if "transcript_empty" in text:
        return "NO_SPEECH_DETECTED"
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
    if "invalid data" in text or "moov atom not found" in text or "could not find codec parameters" in text:
        return "MEDIA_INVALID"
    if "ffmpeg" in text or "audio" in text:
        return "AUDIO_PROCESSING_ERROR"
    return "GPU_PROCESSING_FAILED"


def is_retryable_error_code(code: str) -> bool:
    return code in {"MEDIA_NOT_FOUND", "CUDA_UNAVAILABLE", "AUDIO_PROCESSING_ERROR"}


def retry_delay_seconds(attempt: int) -> float:
    base = {1: 5, 2: 15, 3: 30, 4: 60, 5: 120}.get(attempt, 300)
    return base + (attempt * 0.37)



class GpuWorker:
    def __init__(self, heartbeat: AsyncHeartbeat | None = None) -> None:
        self._semaphore = asyncio.Semaphore(1)
        self._service = ProcessingService()
        self._repository = JobRepository()
        self._gpu_lease = PostgresGpuLease(self._repository.conninfo)
        self._heartbeat = heartbeat

    def close(self) -> None:
        self._service.close()
        self._repository.close()

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
            job_type = self._repository.job_type(job_id) or str(message.get("job_type") or "TRANSCRIBE")
            asr_only_job = job_type == "TRANSCRIBE_ASR"
            enrichment_job = job_type == "TRANSCRIPT_ENRICH"
            self._repository.update_job(job_id, "RUNNING", "TRANSCRIBING", 20)
            request_profile = "asr" if asr_only_job else ("enrich" if enrichment_job else message.get("profile", "meeting"))
            input_transcript = None
            if enrichment_job:
                transcript_id = message.get("transcript_id") or await asyncio.to_thread(self._repository.input_transcript_id, job_id)
                input_transcript = await asyncio.to_thread(self._repository.load_transcript_source, transcript_id)
                if not input_transcript or not input_transcript.get("segments"):
                    raise RuntimeError("TRANSCRIPT_INPUT_NOT_FOUND")
            source_quality = (input_transcript or {}).get("quality_metadata") or {}
            normalized_language = str(message.get("language") or source_quality.get("language") or "ru").strip() or "ru"
            technical_intervals = tuple(await asyncio.to_thread(self._repository.technical_intervals, str(message["meeting_id"]))) if asr_only_job else ()
            request = ProcessingRequest(
                job_id=job_id,
                media_path=resolve_storage_path(str(message["storage_key"])),
                language=normalized_language,
                profile=request_profile,
                min_speakers=int(message.get("min_speakers", 1)),
                max_speakers=int(message.get("max_speakers", 12)),
                input_transcript=input_transcript,
                source_storage_key=str(message.get("storage_key") or "") or None,
                source_audio_hash=str(source_quality.get("asr_audio_hash") or "") or None,
                acoustic_profile=str(message.get("acousticProfile") or message.get("acoustic_profile") or "AUTO").upper(),
                technical_intervals=technical_intervals,
            )

            def progress(stage: str, value: int) -> None:
                LOGGER.info("job=%s stage=%s progress=%s", job_id, stage, value)
                self._repository.update_job(job_id, "RUNNING", stage, value)

            failure_code: str | None = None
            draft_holder: dict[str, str | None] = {"id": None}

            def persist_asr_draft(draft: dict[str, Any]) -> None:
                draft_holder["id"] = self._repository.persist_asr_draft(job_id, str(message["meeting_id"]), draft)

            try:
                if await resident_llm_detected():
                    raise ResidentLlmConflict("resident_llama_server_must_be_stopped_before_transcription")
                LOGGER.info("job=%s waiting for GPU lease path=%s", job_id, request.media_path)
                async with self._gpu_lease:
                    LOGGER.info("job=%s acquired GPU lease", job_id)
                    oom_attempt = 0
                    while True:
                        try:
                            draft_callback = None if enrichment_job else persist_asr_draft
                            result = await asyncio.to_thread(self._service.process, request, progress, draft_callback)
                            break
                        except Exception as exc:
                            if error_code_for(exc) != "CUDA_OOM" or oom_attempt >= 1:
                                raise
                            oom_attempt += 1
                            LOGGER.warning("job=%s CUDA_OOM; cleared resident pipeline and retrying once", job_id)
                LOGGER.info("job=%s released GPU lease", job_id)
                payload = result.to_dict()
                payload["correlation_id"] = message.get("correlation_id") or payload.get("correlation_id")
                payload["meeting_id"] = str(message["meeting_id"])
                payload["processing_job_id"] = job_id
                if asr_only_job:
                    # The callback persisted PARTIAL_READY V1 immediately
                    # after ASR. Closing this job must not create a second
                    # transcript version; enrichment is a separate job.
                    if not draft_holder["id"]:
                        draft_holder["id"] = self._repository.persist_asr_draft(job_id, str(message["meeting_id"]), payload)
                    await asyncio.to_thread(self._repository.complete_asr_job, job_id, str(message["meeting_id"]))
                    payload["transcript_id"] = draft_holder["id"]
                    payload["version_kind"] = "ASR_DRAFT"
                    LOGGER.info("ASR draft ready transcript=%s meeting_id=%s job_id=%s", draft_holder["id"], payload["meeting_id"], job_id)
                    return payload
                if draft_holder["id"]:
                    payload["source_transcript_id"] = draft_holder["id"]
                    payload["version_kind"] = "ENRICHED"
                elif enrichment_job:
                    payload["source_transcript_id"] = message.get("transcript_id") or self._repository.input_transcript_id(job_id)
                    payload["version_kind"] = "ENRICHED"
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
                if is_retryable_error_code(failure_code):
                    scheduled_attempt = self._repository.schedule_retry(
                        job_id,
                        type(exc).__name__ + ": " + str(exc),
                        failure_code + "_RETRY_PENDING",
                        message_id=str(message.get("message_id", "")),
                    )
                    if scheduled_attempt is not None:
                        failure_code = failure_code + "_RETRY_PENDING"
                        if self._heartbeat:
                            self._heartbeat.set_state("READY", failure_code)
                        raise RetryScheduled(retry_delay_seconds(scheduled_attempt)) from exc
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
    worker = GpuWorker(heartbeat)
    recovered = await asyncio.to_thread(worker._repository.reset_stale_leases)
    if recovered:
        LOGGER.warning("recovered stale GPU jobs count=%s", recovered)
    jetstream = client.jetstream()
    await ensure_stream(jetstream, name="WHISPERX", subjects=["media.ingest", "ml.transcribe", "llm.summarize", "llm.assistant"])
    subscription = await jetstream.pull_subscribe("ml.transcribe", durable="whisperx-gpu")
    heartbeat.set_state("READY")
    while True:
        # Keep large-v3 resident between jobs, but release it after the
        # configured idle window so another GPU workload can make progress.
        worker._service.release_idle()
        for message in await fetch_available(subscription, nats.errors.TimeoutError):
            job_id: str | None = None
            try:
                payload = json.loads(message.data)
            except (json.JSONDecodeError, TypeError, UnicodeDecodeError):
                LOGGER.exception("gpu_poison_message_discarded")
                await message.ack()
                continue
            if not isinstance(payload, dict) or not str(payload.get("job_id", "")).strip():
                LOGGER.error("gpu_poison_message_discarded reason=job_id_missing")
                await message.ack()
                continue
            try:
                job_id = str(payload.get("job_id", ""))
                message_id = str(payload.get("message_id", ""))
                async with maintain_message(message, on_tick=lambda: asyncio.to_thread(worker._repository.renew_lease, job_id, message_id)):
                    await worker.handle(payload)
                await message.ack()
            except ResidentLlmConflict:
                # The job is deliberately returned to QUEUED by handle(). A
                # delayed NAK prevents a resident llama-server from turning
                # the pull consumer into a tight redelivery loop.
                await message.nak(delay=RESIDENT_LLM_RETRY_DELAY_SECONDS)
            except RetryScheduled as exc:
                await message.nak(delay=exc.delay_seconds)
            except Exception:
                LOGGER.exception("gpu_message_failed job_id=%s", job_id)
                await message.nak()


if __name__ == "__main__":
    asyncio.run(run())
