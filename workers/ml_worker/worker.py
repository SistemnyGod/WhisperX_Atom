from __future__ import annotations

import asyncio
import json
import os
from pathlib import Path
from typing import Any

from workers.nats_utils import fetch_available
from whisperx_atom.contracts import ProcessingRequest
from whisperx_atom.processing import ProcessingService
from .persistence import JobRepository


class GpuWorker:
    def __init__(self) -> None:
        self._semaphore = asyncio.Semaphore(1)
        self._service = ProcessingService()
        self._repository = JobRepository()

    async def handle(self, message: dict[str, Any]) -> dict[str, Any] | None:
        async with self._semaphore:
            job_id = str(message["job_id"])
            if not self._repository.claim_message(str(message.get("message_id", "")), job_id):
                state = self._repository.job_state(job_id)
                if state is not None and state[0] not in ("READY", "FAILED"):
                    raise RuntimeError("message_claimed_by_active_worker")
                return None
            state = self._repository.job_state(job_id)
            if state is not None and state[0] == "READY":
                return None
            self._repository.update_job(job_id, "RUNNING", "TRANSCRIBING", 20)
            request = ProcessingRequest(job_id=job_id, media_path=Path(message["storage_key"]), language=message.get("language", "ru"), profile=message.get("profile", "meeting"), min_speakers=int(message.get("min_speakers", 1)), max_speakers=int(message.get("max_speakers", 12)))

            def progress(stage: str, value: int) -> None:
                self._repository.update_job(job_id, "RUNNING", stage, value)

            try:
                result = await asyncio.to_thread(self._service.process, request, progress)
                payload = result.to_dict()
                await asyncio.to_thread(self._repository.persist_result, job_id, str(message["meeting_id"]), payload)
                return payload
            except Exception as exc:
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
        await jetstream.add_stream(name="WHISPERX", subjects=["media.ingest", "ml.transcribe"])
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


