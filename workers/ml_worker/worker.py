from __future__ import annotations

import asyncio
import json
import logging
import os
from pathlib import Path
from typing import Any

from workers.nats_utils import fetch_available
from whisperx_atom.contracts import ProcessingRequest
from whisperx_atom.processing import ProcessingService
from workers.gpu_lease import PostgresGpuLease
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


def error_code_for(exc: Exception) -> str:
    text = f"{type(exc).__name__}: {exc}".lower()
    if "cuda" in text and ("out of memory" in text or "oom" in text):
        return "CUDA_OOM"
    if isinstance(exc, FileNotFoundError):
        return "MEDIA_NOT_FOUND"
    if "hf_token" in text or "huggingface" in text or "gated" in text:
        return "MODEL_ACCESS_ERROR"
    if "ffmpeg" in text or "audio" in text:
        return "AUDIO_PROCESSING_ERROR"
    return "GPU_PROCESSING_FAILED"



class GpuWorker:
    def __init__(self) -> None:
        self._semaphore = asyncio.Semaphore(1)
        self._service = ProcessingService()
        self._repository = JobRepository()
        self._gpu_lease = PostgresGpuLease(self._repository.conninfo)

    async def handle(self, message: dict[str, Any]) -> dict[str, Any] | None:
        async with self._semaphore:
            job_id = str(message["job_id"])
            LOGGER.info("received job=%s message=%s stage=%s", job_id, message.get("message_id"), message.get("stage"))
            if not self._repository.claim_message(str(message.get("message_id", "")), job_id):
                state = self._repository.job_state(job_id)
                if state is not None and state[0] not in ("READY", "FAILED", "CANCELLED"):
                    raise RuntimeError("message_claimed_by_active_worker")
                return None
            state = self._repository.job_state(job_id)
            if state is not None and state[0] in ("READY", "FAILED", "CANCELLED"):
                LOGGER.info("skip terminal job=%s status=%s", job_id, state[0])
                return None
            self._repository.update_job(job_id, "RUNNING", "TRANSCRIBING", 20)
            request = ProcessingRequest(job_id=job_id, media_path=Path(message["storage_key"]), language=message.get("language", "ru"), profile=message.get("profile", "meeting"), min_speakers=int(message.get("min_speakers", 1)), max_speakers=int(message.get("max_speakers", 12)))

            def progress(stage: str, value: int) -> None:
                LOGGER.info("job=%s stage=%s progress=%s", job_id, stage, value)
                self._repository.update_job(job_id, "RUNNING", stage, value)

            try:
                if await resident_llm_detected():
                    raise ResidentLlmConflict("resident_llama_server_must_be_stopped_before_transcription")
                LOGGER.info("job=%s waiting for GPU lease path=%s", job_id, request.media_path)
                async with self._gpu_lease:
                    LOGGER.info("job=%s acquired GPU lease", job_id)
                    result = await asyncio.to_thread(self._service.process, request, progress)
                LOGGER.info("job=%s released GPU lease", job_id)
                payload = result.to_dict()
                await asyncio.to_thread(self._repository.persist_result, job_id, str(message["meeting_id"]), payload)
                LOGGER.info("job=%s persisted segments=%s words=%s", job_id, len(payload.get("segments", [])), len(payload.get("word_segments", [])))
                return payload
            except ResidentLlmConflict as exc:
                LOGGER.warning("job=%s waiting: %s", job_id, exc)
                self._repository.release_message(str(message.get("message_id", "")))
                self._repository.update_job(job_id, "QUEUED", "WAITING_FOR_GPU", 0, str(exc), "GPU_RESIDENT_LLM_CONFLICT")
                raise
            except Exception as exc:
                LOGGER.exception("job=%s failed", job_id)
                self._repository.release_message(str(message.get("message_id", "")))
                self._repository.update_job(job_id, "FAILED", "FAILED", 0, type(exc).__name__ + ": " + str(exc), error_code_for(exc))
                raise


async def run() -> None:
    try:
        import nats
    except ImportError as exc:
        raise RuntimeError("Install workers/ml_worker/requirements.txt") from exc

    worker = GpuWorker()
    client = await nats.connect(os.getenv("NATS_URL", "nats://nats:4222"))
    jetstream = client.jetstream()
    try:
        await jetstream.add_stream(name="WHISPERX", subjects=["media.ingest", "ml.transcribe", "llm.summarize"])
    except Exception:
        pass
    subscription = await jetstream.pull_subscribe("ml.transcribe", durable="whisperx-gpu")
    while True:
        for message in await fetch_available(subscription, nats.errors.TimeoutError):
            try:
                await worker.handle(json.loads(message.data))
                await message.ack()
            except Exception:
                await message.nak()


if __name__ == "__main__":
    asyncio.run(run())


