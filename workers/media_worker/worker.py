from __future__ import annotations

import asyncio
import json
import os
import logging
import time
from pathlib import Path

from workers.nats_utils import fetch_available, maintain_message
from workers.runtime_heartbeat import AsyncHeartbeat

from .media_worker import prepare_media
from .persistence import claim_message, job_attempt, job_state, mark_ready_for_asr_and_enqueue, record_stage_timing, release_message, reset_media_leases, renew_lease, schedule_media_retry, update_asset, update_job, update_recording_session_state
from .recording_assembly import assemble_recording_session
from .retry import RETRY_DELAY_SECONDS, classify_media_failure, should_retry


async def run() -> None:
    logger = logging.getLogger("media_worker")
    try:
        import nats
    except ImportError as exc:
        raise RuntimeError("Install workers/media_worker/requirements.txt") from exc

    client = await nats.connect(os.getenv("NATS_URL", "nats://nats:4222"))
    heartbeat = AsyncHeartbeat("media-worker", capabilities=lambda: {"natsConnected": True, "mediaPipeline": "ready"})
    await heartbeat.start()
    heartbeat.set_state("READY")
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
            correlation_id = payload.get("correlation_id")
            meeting_id = payload.get("meeting_id")
            logger.info("media_message correlation_id=%s meeting_id=%s job_id=%s", correlation_id, meeting_id, job_id)
            heartbeat.set_job(str(job_id))
            heartbeat.set_state("BUSY")
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
                async with maintain_message(message, on_tick=lambda: asyncio.to_thread(renew_lease, str(job_id), message_id)):
                    update_job(job_id, "RUNNING", "VALIDATING", 5)
                    source_type = payload.get("source_type")
                    if source_type == "recorder_session":
                        session_id = payload.get("session_id")
                        if not session_id:
                            raise ValueError("recording_session_id_required")
                        await asyncio.to_thread(update_recording_session_state, str(session_id), "ASSEMBLING")
                        assembly_started = time.monotonic()
                        source = await asyncio.to_thread(assemble_recording_session, str(session_id), root / "assembled" / job_id)
                        await asyncio.to_thread(record_stage_timing, str(session_id), "assembly_ms", int((time.monotonic() - assembly_started) * 1000))
                        await asyncio.to_thread(update_recording_session_state, str(session_id), "ASSEMBLED")
                    else:
                        source = Path(payload["storage_key"])
                    prepare_started = time.monotonic()
                    derivatives = await asyncio.to_thread(prepare_media, source, root / "derived" / job_id)
                    if source_type == "recorder_session":
                        await asyncio.to_thread(record_stage_timing, str(session_id), "media_prepare_ms", int((time.monotonic() - prepare_started) * 1000))
                    update_job(job_id, "RUNNING", "NORMALIZING", 15)
                    update_asset(payload["media_asset_id"], derivatives.sha256, str(derivatives.archive_flac), str(derivatives.preview_opus), str(derivatives.asr_wav), derivatives.duration_ms)
                    if source_type == "recorder_session":
                        await asyncio.to_thread(update_recording_session_state, str(session_id), "MEDIA_READY")
                    next_message = dict(payload)
                    next_message["message_id"] = str(__import__("uuid").uuid4())
                    next_message.update({
                        "stage": "READY_FOR_ASR",
                        "storage_key": str(derivatives.asr_wav),
                        "archive_storage_key": str(derivatives.archive_flac),
                        "preview_storage_key": str(derivatives.preview_opus),
                        "sha256": derivatives.sha256,
                        "duration_ms": derivatives.duration_ms,
                        "audio_quality": derivatives.quality_report,
                    })
                    await asyncio.to_thread(mark_ready_for_asr_and_enqueue, job_id, next_message)
                    await message.ack()
            except Exception as exc:
                failure = classify_media_failure(exc)
                attempt = await asyncio.to_thread(job_attempt, str(job_id))
                session_id = str(payload["session_id"]) if payload.get("source_type") == "recorder_session" and payload.get("session_id") else None
                if should_retry(attempt, failure) and await asyncio.to_thread(schedule_media_retry, str(job_id), session_id, str(exc), failure.code):
                    # Release the inbox lease first, then ask JetStream for one
                    # delayed redelivery. The DB attempt guard makes duplicate
                    # deliveries unable to create another retry.
                    await asyncio.to_thread(release_message, message_id)
                    await message.nak(delay=RETRY_DELAY_SECONDS)
                    heartbeat.set_state("READY", failure.code)
                    continue
                await asyncio.to_thread(release_message, message_id)
                if session_id:
                    try:
                        await asyncio.to_thread(update_recording_session_state, session_id, "MEDIA_FAILED")
                    except Exception:
                        pass
                update_job(job_id, "FAILED", "FAILED", 0, str(exc), failure.code)
                await message.ack()
                heartbeat.set_state("READY", failure.code)
            finally:
                active_jobs.discard(job_id)
                heartbeat.set_job(None)
                heartbeat.set_state("READY")


if __name__ == "__main__":
    asyncio.run(run())
