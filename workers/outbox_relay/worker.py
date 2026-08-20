from __future__ import annotations

import asyncio
import json
import os

import psycopg
from workers.nats_utils import ensure_stream
from workers.runtime_heartbeat import AsyncHeartbeat


async def recover_expired(connection) -> None:
    """Repair events lost between an outbox publish and worker ACK.

    JetStream normally redelivers an unacked message, but an API/NATS/worker
    restart can leave a durable job with a published outbox row and no live
    inbox lease.  Re-emitting that event is safe: every downstream stage uses
    the job/transcript id as its idempotency key.  We deliberately select only
    stale RUNNING jobs and lock rows so two relays cannot repair the same job
    concurrently.
    """
    stale_seconds = max(30, int(os.getenv("OUTBOX_STALE_LEASE_SECONDS", "90")))
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
