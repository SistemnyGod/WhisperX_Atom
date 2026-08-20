from __future__ import annotations

import asyncio
import json
import os
from datetime import datetime, timedelta, timezone

import psycopg
from workers.nats_utils import ensure_stream
from workers.runtime_heartbeat import AsyncHeartbeat


def _watchdog_seconds(name: str, default: int, minimum: int) -> int:
    try:
        configured = int(os.getenv(name, str(default)))
    except (TypeError, ValueError):
        configured = default
    return max(minimum, configured)


def _watchdog_backoff_seconds(requeue_count: int, base_seconds: int) -> int:
    """Return the bounded 2/5/15/30 minute watchdog schedule."""
    schedule = (base_seconds, 300, 900, 1800)
    index = min(max(int(requeue_count), 0), len(schedule) - 1)
    return max(base_seconds, schedule[index])


def recover_starved_queued(connection) -> int:
    """Re-emit one durable event for a QUEUED job no worker has claimed.

    A published outbox row only proves that the relay handed a message to
    JetStream. If NATS or a consumer disappeared immediately afterwards, the
    job can otherwise remain QUEUED forever. Re-emission uses a bounded
    2/5/15/30 minute backoff per durable job and never steals a job with an
    active inbox lease. A worker claim or normal retry can still move the job
    out of QUEUED; the counter only limits repeated recovery of the same stall.
    """
    stale_seconds = _watchdog_seconds("OUTBOX_QUEUED_WATCHDOG_SECONDS", 120, 60)
    rows = connection.execute(f"""
        SELECT j.id,j.meeting_id,j.type,j.stage,j.attempt,j.media_asset_id,
               a.storage_key,a.asr_storage_key,a.source_type,
               j.input_transcript_id,t.language,t.quality_metadata,
               j.pipeline_correlation_id,j.watchdog_requeue_count,
               j.last_watchdog_requeue_at
        FROM jobs j
        LEFT JOIN media_assets a ON a.id=j.media_asset_id
        LEFT JOIN transcripts t ON t.id=j.input_transcript_id
        WHERE j.status='QUEUED'
          AND j.updated_at < now() - interval '{stale_seconds} seconds'
          -- The marker is only a debounce window, not a terminal state.  A
          -- relay can mark the job and crash after the original outbox row
          -- was marked published but before JetStream made it visible to a
          -- consumer. Allow another emission once the watchdog interval has
          -- elapsed; the Python backoff then applies the longer recovery
          -- window for jobs that have already been re-emitted repeatedly.
          AND (
              COALESCE(j.error_code,'') <> 'QUEUED_WATCHDOG_REQUEUED'
              OR j.updated_at < now() - interval '{stale_seconds} seconds'
          )
          AND j.type IN ('TRANSCRIBE','TRANSCRIBE_ASR','TRANSCRIBE_REPROCESS','TRANSCRIPT_ENRICH','SUMMARIZE')
          AND NOT EXISTS (
              SELECT 1 FROM inbox_messages i
              WHERE i.job_id=j.id AND i.lease_expires_at > now()
          )
        ORDER BY j.updated_at
        FOR UPDATE OF j SKIP LOCKED
    """).fetchall()
    recovered = 0
    for (
        job_id, meeting_id, job_type, stage, attempt, asset_id,
        storage_key, asr_storage_key, source_type,
        transcript_id, language, quality_metadata, correlation_id,
        watchdog_requeue_count, last_watchdog_requeue_at,
    ) in rows:
        if last_watchdog_requeue_at is not None:
            last = last_watchdog_requeue_at
            if last.tzinfo is None:
                last = last.replace(tzinfo=timezone.utc)
            wait_seconds = _watchdog_backoff_seconds(watchdog_requeue_count or 0, stale_seconds)
            if datetime.now(timezone.utc) - last < timedelta(seconds=wait_seconds):
                continue
        metadata = quality_metadata if isinstance(quality_metadata, dict) else {}
        normalized_type = str(job_type)
        normalized_stage = str(stage or "")
        if normalized_type == "SUMMARIZE":
            topic = "llm.summarize"
            payload = {
                "message_id": str(__import__("uuid").uuid4()),
                "job_id": str(job_id), "meeting_id": str(meeting_id),
                "transcript_id": str(transcript_id) if transcript_id else None,
                "source_hash": metadata.get("asr_audio_hash"),
                "correlation_id": str(correlation_id) if correlation_id else None,
            }
        elif normalized_type == "TRANSCRIBE" and normalized_stage in {"INGEST", "UPLOADED", "VALIDATING", "NORMALIZING"}:
            topic = "media.ingest"
            payload = {
                "message_id": str(__import__("uuid").uuid4()),
                "job_id": str(job_id), "meeting_id": str(meeting_id),
                "media_asset_id": str(asset_id) if asset_id else None,
                "stage": normalized_stage or "UPLOADED", "attempt": int(attempt),
                "storage_key": storage_key, "source_type": source_type,
                "correlation_id": str(correlation_id) if correlation_id else None,
            }
        else:
            topic = "ml.transcribe"
            payload = {
                "message_id": str(__import__("uuid").uuid4()),
                "job_id": str(job_id), "meeting_id": str(meeting_id),
                "media_asset_id": str(asset_id) if asset_id else None,
                "transcript_id": str(transcript_id) if transcript_id else None,
                "stage": normalized_stage or "ASR_READY", "attempt": int(attempt),
                "storage_key": asr_storage_key or metadata.get("asr_storage_key"),
                "source_type": source_type, "language": str(language or metadata.get("language") or "ru"),
                "acousticProfile": str(metadata.get("acoustic_profile") or "AUTO"),
                "correlation_id": str(correlation_id) if correlation_id else None,
            }
        connection.execute(
            """
            UPDATE jobs
            SET error_code='QUEUED_WATCHDOG_REQUEUED',
                watchdog_requeue_count=COALESCE(watchdog_requeue_count,0)+1,
                last_watchdog_requeue_at=now(),
                last_heartbeat=now(),
                updated_at=now()
            WHERE id=%s AND status='QUEUED'
            """,
            (job_id,),
        )
        pending = connection.execute(
            "SELECT EXISTS(SELECT 1 FROM outbox_messages WHERE topic=%s AND payload->>'job_id'=%s AND published_at IS NULL)",
            (topic, str(job_id)),
        ).fetchone()[0]
        if not pending:
            # Do not rely on the historical row: it may already be marked
            # published even though NATS/JetStream lost the hand-off.  A new
            # message id is intentional; consumers deduplicate by durable
            # job/transcript id, while the relay gets one fresh delivery
            # opportunity.
            connection.execute(
                "INSERT INTO outbox_messages(id,topic,payload) VALUES(gen_random_uuid(),%s,%s::jsonb)",
                (topic, json.dumps(payload)),
            )
        recovered += 1
    return recovered


async def recover_expired(connection) -> None:
    """Repair events lost between an outbox publish and worker ACK.

    JetStream normally redelivers an unacked message, but an API/NATS/worker
    restart can leave a durable job with a published outbox row and no live
    inbox lease.  Re-emitting that event is safe: every downstream stage uses
    the job/transcript id as its idempotency key.  We deliberately select only
    stale RUNNING jobs and lock rows so two relays cannot repair the same job
    concurrently.
    """
    stale_seconds = _watchdog_seconds("OUTBOX_STALE_LEASE_SECONDS", 90, 30)
    now_stale = f"(j.last_heartbeat IS NULL OR j.lease_expires_at IS NULL OR j.lease_expires_at < now() OR j.last_heartbeat < now() - interval '{stale_seconds} seconds')"

    media_rows = connection.execute(f"""
        SELECT j.id,j.meeting_id,j.media_asset_id,j.attempt,a.storage_key,a.source_type,
               p.recording_session_id
        FROM jobs j
        JOIN media_assets a ON a.id=j.media_asset_id
        LEFT JOIN recording_pipeline_runs p ON p.media_asset_id=a.id
        WHERE j.type IN ('TRANSCRIBE','TRANSCRIBE_ASR')
          AND (
              (j.status='RUNNING' AND {now_stale})
              OR (j.status='QUEUED' AND j.error_code='WORKER_RESTART_RECOVERY' AND j.lease_expires_at IS NULL)
          )
          AND j.stage IN ('INGEST','UPLOADED','VALIDATING','NORMALIZING')
          AND NOT EXISTS (
              SELECT 1 FROM inbox_messages i
              WHERE i.job_id=j.id AND i.lease_expires_at > now()
          )
        FOR UPDATE OF j SKIP LOCKED
    """).fetchall()
    for job_id, meeting_id, asset_id, attempt, storage_key, source_type, session_id in media_rows:
        connection.execute(
            "UPDATE jobs SET status='QUEUED',stage='UPLOADED',progress=0,worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,error_code='WORKER_RESTART_RECOVERY',updated_at=now() WHERE id=%s",
            (job_id,),
        )
        exists = connection.execute(
            "SELECT EXISTS(SELECT 1 FROM outbox_messages WHERE topic='media.ingest' AND payload->>'job_id'=%s AND published_at IS NULL)",
            (str(job_id),),
        ).fetchone()[0]
        if not exists:
            payload = json.dumps({
                "message_id": str(__import__("uuid").uuid4()),
                "job_id": str(job_id), "meeting_id": str(meeting_id), "media_asset_id": str(asset_id),
                "stage": "UPLOADED", "attempt": int(attempt), "storage_key": storage_key,
                "source_type": source_type, "session_id": str(session_id) if session_id else None,
            })
            connection.execute("INSERT INTO outbox_messages(id,topic,payload) VALUES(gen_random_uuid(),'media.ingest',%s::jsonb)", (payload,))

    enrich_rows = connection.execute(f"""
        SELECT j.id,j.meeting_id,j.input_transcript_id,j.pipeline_correlation_id,
               t.language,t.quality_metadata
        FROM jobs j
        JOIN transcripts t ON t.id=j.input_transcript_id
        WHERE j.type='TRANSCRIPT_ENRICH'
          AND (
              (j.status='RUNNING' AND {now_stale})
              OR (j.status='QUEUED' AND j.error_code='WORKER_RESTART_RECOVERY' AND j.lease_expires_at IS NULL)
          )
          AND NOT EXISTS (
              SELECT 1 FROM inbox_messages i
              WHERE i.job_id=j.id AND i.lease_expires_at > now()
          )
        FOR UPDATE OF j SKIP LOCKED
    """).fetchall()
    for job_id, meeting_id, transcript_id, correlation_id, language, quality_metadata in enrich_rows:
        metadata = quality_metadata if isinstance(quality_metadata, dict) else {}
        storage_key = metadata.get("asr_storage_key")
        connection.execute(
            "UPDATE jobs SET status='QUEUED',stage='ASR_READY',progress=0,worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,error_code='WORKER_RESTART_RECOVERY',updated_at=now() WHERE id=%s",
            (job_id,),
        )
        exists = connection.execute(
            "SELECT EXISTS(SELECT 1 FROM outbox_messages WHERE topic='ml.transcribe' AND payload->>'job_id'=%s AND published_at IS NULL)",
            (str(job_id),),
        ).fetchone()[0]
        if not exists:
            payload = json.dumps({
                "message_id": str(__import__("uuid").uuid4()), "job_id": str(job_id),
                "meeting_id": str(meeting_id), "transcript_id": str(transcript_id),
                "stage": "ASR_READY", "storage_key": storage_key,
                "language": str(language or metadata.get("language") or "ru"),
                "acousticProfile": str(metadata.get("acoustic_profile") or "AUTO"),
                "correlation_id": str(correlation_id) if correlation_id else None,
            })
            connection.execute("INSERT INTO outbox_messages(id,topic,payload) VALUES(gen_random_uuid(),'ml.transcribe',%s::jsonb)", (payload,))

    summary_rows = connection.execute(f"""
        SELECT j.id,j.meeting_id,j.input_transcript_id,j.pipeline_correlation_id,
               t.quality_metadata
        FROM jobs j
        JOIN transcripts t ON t.id=j.input_transcript_id
        WHERE j.type='SUMMARIZE'
          AND (
              (j.status='RUNNING' AND {now_stale})
              OR (j.status='QUEUED' AND j.error_code='WORKER_RESTART_RECOVERY' AND j.lease_expires_at IS NULL)
          )
          AND NOT EXISTS (
              SELECT 1 FROM inbox_messages i
              WHERE i.job_id=j.id AND i.lease_expires_at > now()
          )
        FOR UPDATE OF j SKIP LOCKED
    """).fetchall()
    for job_id, meeting_id, transcript_id, correlation_id, quality_metadata in summary_rows:
        metadata = quality_metadata if isinstance(quality_metadata, dict) else {}
        connection.execute(
            "UPDATE jobs SET status='QUEUED',stage='TRANSCRIPT_READY',progress=0,worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,error_code='WORKER_RESTART_RECOVERY',updated_at=now() WHERE id=%s",
            (job_id,),
        )
        exists = connection.execute(
            "SELECT EXISTS(SELECT 1 FROM outbox_messages WHERE topic='llm.summarize' AND payload->>'job_id'=%s AND published_at IS NULL)",
            (str(job_id),),
        ).fetchone()[0]
        if not exists:
            payload = json.dumps({
                "message_id": str(__import__("uuid").uuid4()), "job_id": str(job_id),
                "meeting_id": str(meeting_id), "transcript_id": str(transcript_id),
                "source_hash": metadata.get("asr_audio_hash"),
                "correlation_id": str(correlation_id) if correlation_id else None,
            })
            connection.execute("INSERT INTO outbox_messages(id,topic,payload) VALUES(gen_random_uuid(),'llm.summarize',%s::jsonb)", (payload,))


async def run() -> None:
    try:
        import nats
    except ImportError as exc:
        raise RuntimeError("Install workers/outbox_relay/requirements.txt") from exc

    conninfo = os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")
    client = await nats.connect(os.getenv("NATS_URL", "nats://nats:4222"))
    heartbeat = AsyncHeartbeat("outbox-relay", capabilities=lambda: {"natsConnected": True, "outboxRelay": "ready"})
    await heartbeat.start()
    jetstream = client.jetstream()
    subjects = ["media.ingest", "ml.transcribe", "llm.summarize", "llm.assistant"]
    # Existing development streams may have been created before a new subject
    # was introduced. Update the subject set instead of silently keeping stale
    # configuration (which would block the oldest outbox message forever).
    await ensure_stream(jetstream, name="WHISPERX", subjects=subjects)
    heartbeat.set_state("READY")

    while True:
        published = False
        with psycopg.connect(conninfo) as connection:
            recovered = recover_starved_queued(connection)
            if recovered:
                print(f"outbox_queued_watchdog_requeued={recovered}", flush=True)
            await recover_expired(connection)
            row = connection.execute(
                "SELECT id, topic, payload FROM outbox_messages WHERE published_at IS NULL ORDER BY created_at, id LIMIT 1"
            ).fetchone()

        if row:
            message_id, topic, payload = row
            # Do not hold a PostgreSQL transaction/row lock while waiting on
            # NATS. A relay restart or a slow broker must not stall writers
            # inserting new outbox rows. NATS de-duplicates the stable message
            # id, so two relay instances racing on the same row are harmless.
            await jetstream.publish(
                topic,
                json.dumps(payload).encode("utf-8"),
                headers={"Nats-Msg-Id": str(message_id)},
            )
            with psycopg.connect(conninfo) as connection:
                connection.execute(
                    "UPDATE outbox_messages SET published_at=now() WHERE id=%s AND published_at IS NULL",
                    (message_id,),
                )
            published = True
        if not published:
            await asyncio.sleep(0.5)


if __name__ == "__main__":
    asyncio.run(run())
