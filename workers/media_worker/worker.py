from __future__ import annotations

import asyncio
import json
import os
from pathlib import Path

from workers.nats_utils import fetch_available

from .media_worker import prepare_media
from .persistence import claim_message, job_state, release_message, update_asset, update_job


async def run() -> None:
    try:
        import nats
    except ImportError as exc:
        raise RuntimeError("Install workers/media_worker/requirements.txt") from exc

    client = await nats.connect(os.getenv("NATS_URL", "nats://nats:4222"))
    jetstream = client.jetstream()
    try:
        await jetstream.add_stream(name="WHISPERX", subjects=["media.ingest", "ml.transcribe"])
    except Exception:
        pass
    subscription = await jetstream.pull_subscribe("media.ingest", durable="whisperx-media")
    root = Path(os.getenv("MEDIA_ROOT", "/data"))
    while True:
        for message in await fetch_available(subscription, nats.errors.TimeoutError):
            payload = json.loads(message.data)
            job_id = payload["job_id"]
            message_id = str(payload.get("message_id", ""))
            if not claim_message(message_id, job_id):
                state = job_state(job_id)
                if state is None or state[0] in ("READY", "FAILED") or state[1] == "READY_FOR_ASR":
                    await message.ack()
                else:
                    await message.nak()
                continue
            state = job_state(job_id)
            if state is not None and state[1] in ("READY_FOR_ASR", "TRANSCRIBING", "ALIGNING", "DIARIZING", "QUALITY_CHECK", "PERSISTING", "READY"):
                await message.ack()
                continue
            try:
                update_job(job_id, "RUNNING", "VALIDATING", 5)
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
                await jetstream.publish("ml.transcribe", json.dumps(next_message).encode("utf-8"))
                update_job(job_id, "QUEUED", "READY_FOR_ASR", 25)
                await message.ack()
            except Exception as exc:
                release_message(message_id)
                update_job(job_id, "FAILED", "FAILED", 0, type(exc).__name__ + ": " + str(exc), "MEDIA_PROCESSING_FAILED")
                await message.nak()


if __name__ == "__main__":
    asyncio.run(run())


