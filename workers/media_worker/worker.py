from __future__ import annotations

import asyncio
import json
import os
from pathlib import Path

from workers.nats_utils import fetch_available

from .media_worker import prepare_media
from .persistence import claim_message, job_state, mark_ready_for_asr_and_enqueue, release_message, reset_media_leases, update_asset, update_job
from .recording_assembly import assemble_recording_session


async def run() -> None:
    try:
        import nats
    except ImportError as exc:
        raise RuntimeError("Install workers/media_worker/requirements.txt") from exc

    client = await nats.connect(os.getenv("NATS_URL", "nats://nats:4222"))
    await asyncio.to_thread(reset_media_leases)
    jetstream = client.jetstream()
    try:
        await jetstream.add_stream(name="WHISPERX", subjects=["media.ingest", "ml.transcribe", "llm.summarize", "llm.assistant"])
    except Exception:
        pass
    subscription = await jetstream.pull_subscribe("media.ingest", durable="whisperx-media")
    root = Path(os.getenv("MEDIA_ROOT", "/data"))
    active_jobs: set[str] = set()
    while True:
        for message in await fetch_available(subscription, nats.errors.TimeoutError):
            payload = json.loads(message.data)
            job_id = payload["job_id"]
            message_id = str(payload.get("message_id", ""))
            if not claim_message(message_id, job_id):
                state = job_state(job_id)
                if state is None or state[0] in ("READY", "FAILED", "CANCELLED") or state[1] == "READY_FOR_ASR":
                    await message.ack()
                else:
                    await message.nak()
                continue
            state = job_state(job_id)
            if state is not None and (state[0] in ("READY", "FAILED", "CANCELLED") or state[1] in ("READY_FOR_ASR", "TRANSCRIBING", "ALIGNING", "DIARIZING", "QUALITY_CHECK", "PERSISTING", "READY")):
                await message.ack()
                continue
            if job_id in active_jobs:
                await message.nak()
                continue
            active_jobs.add(job_id)
            try:
                update_job(job_id, "RUNNING", "VALIDATING", 5)
                source_type = payload.get("source_type")
                if source_type == "recorder_session":
                    session_id = payload.get("session_id")
                    if not session_id:
                        raise ValueError("recording_session_id_required")
                    source = await asyncio.to_thread(assemble_recording_session, str(session_id), root / "assembled" / job_id)
                else:
                    source = Path(payload["storage_key"])
                derivatives = await asyncio.to_thread(prepare_media, source, root / "derived" / job_id)
                update_job(job_id, "RUNNING", "NORMALIZING", 15)
                update_asset(payload["media_asset_id"], derivatives.sha256, str(derivatives.archive_flac), str(derivatives.preview_opus), str(derivatives.asr_wav), derivatives.duration_ms)
                next_message = dict(payload)
                next_message["message_id"] = str(__import__("uuid").uuid4())
                next_message.update({
                    "stage": "READY_FOR_ASR",
                    "storage_key": str(derivatives.asr_wav),
                    "archive_storage_key": str(derivatives.archive_flac),
                    "preview_storage_key": str(derivatives.preview_opus),
                    "sha256": derivatives.sha256,
                    "duration_ms": derivatives.duration_ms,
                })
                await asyncio.to_thread(mark_ready_for_asr_and_enqueue, job_id, next_message)
                await message.ack()
            except Exception as exc:
                release_message(message_id)
                update_job(job_id, "FAILED", "FAILED", 0, type(exc).__name__ + ": " + str(exc), "RECORDING_ASSEMBLY_FAILED" if payload.get("source_type") == "recorder_session" else "MEDIA_PROCESSING_FAILED")
                await message.ack()
            finally:
                active_jobs.discard(job_id)


if __name__ == "__main__":
    asyncio.run(run())


