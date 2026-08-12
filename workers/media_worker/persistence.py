from __future__ import annotations

import json
import os
import socket

import psycopg


def _conninfo() -> str:
    return os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")


def reset_media_leases() -> None:
    """Release leases left by a crashed media worker on process startup.

    Only media ingestion stages are reset. GPU and summary jobs use the same
    inbox table but must not be stolen by this worker.
    """
    with psycopg.connect(_conninfo()) as connection:
        connection.execute(
            """
            UPDATE inbox_messages AS inbox
            SET lease_expires_at=now() - interval '1 second', worker_id=NULL
            FROM jobs AS job
            WHERE inbox.job_id=job.id
              AND job.type='TRANSCRIBE'
              AND job.stage IN ('UPLOADED','VALIDATING','NORMALIZING')
            """
        )

def claim_message(message_id: str | None, job_id: str | None = None) -> bool:
    if not message_id:
        return True
    with psycopg.connect(_conninfo()) as connection:
        row = connection.execute(
            """
            INSERT INTO inbox_messages(message_id, job_id, lease_expires_at, worker_id)
            VALUES(%s, %s, now() + interval '30 minutes', %s)
            ON CONFLICT(message_id) DO UPDATE SET
                job_id=excluded.job_id,
                lease_expires_at=excluded.lease_expires_at,
                worker_id=excluded.worker_id
            WHERE inbox_messages.lease_expires_at IS NULL OR inbox_messages.lease_expires_at < now()
            RETURNING message_id
            """,
            (message_id, job_id, socket.gethostname()),
        ).fetchone()
        return row is not None


def job_state(job_id: str | None) -> tuple[str, str] | None:
    if not job_id:
        return None
    with psycopg.connect(_conninfo()) as connection:
        return connection.execute("SELECT status, stage FROM jobs WHERE id=%s", (job_id,)).fetchone()


def job_attempt(job_id: str) -> int:
    with psycopg.connect(_conninfo()) as connection:
        row = connection.execute("SELECT attempt FROM jobs WHERE id=%s", (job_id,)).fetchone()
        return int(row[0]) if row else 0


def release_message(message_id: str | None) -> None:
    if not message_id:
        return
    with psycopg.connect(_conninfo()) as connection:
        connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (message_id,))


def schedule_media_retry(job_id: str, session_id: str | None, error: str, error_code: str) -> bool:
    """Atomically consume the one allowed retry; returns false on attempt two."""
    with psycopg.connect(_conninfo()) as connection:
        updated = connection.execute(
            """
            UPDATE jobs
            SET status='QUEUED', stage='MEDIA_RETRY_WAIT', progress=0,
                attempt=attempt+1, error_message=%s, error_code=%s,
                worker_id=NULL, lease_expires_at=NULL, last_heartbeat=now(), updated_at=now()
            WHERE id=%s AND status <> 'CANCELLED' AND attempt=0
            """,
            (error, error_code, job_id),
        )
        if updated.rowcount != 1:
            return False
        if session_id:
            connection.execute("UPDATE recording_sessions SET state='MEDIA_RETRY_WAIT' WHERE id=%s", (session_id,))
        return True


def mark_ready_for_asr_and_enqueue(job_id: str, payload: dict) -> bool:
    """Atomically publish the READY_FOR_ASR state and its durable outbox event."""
    with psycopg.connect(_conninfo()) as connection:
        updated = connection.execute(
            "UPDATE jobs SET status='QUEUED',stage='READY_FOR_ASR',progress=25,error_message=NULL,error_code=NULL,worker_id=%s,lease_expires_at=now()+interval '30 minutes',last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'",
            (socket.gethostname(), job_id),
        )
        if updated.rowcount != 1:
            return False
        connection.execute(
            "INSERT INTO outbox_messages(id,topic,payload) VALUES(gen_random_uuid(),'ml.transcribe',%s::jsonb)",
            (json.dumps(payload),),
        )
        return True


def update_job(job_id: str, status: str, stage: str, progress: int, error: str | None = None, error_code: str | None = None) -> None:
    with psycopg.connect(_conninfo()) as connection:
        connection.execute(
            "UPDATE jobs SET status=%s,stage=%s,progress=%s,error_message=%s,error_code=%s,worker_id=%s,lease_expires_at=now()+interval '30 minutes',last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'",
            (status, stage, progress, error, error_code, socket.gethostname(), job_id),
        )


def renew_lease(job_id: str, message_id: str | None = None) -> None:
    with psycopg.connect(_conninfo()) as connection:
        connection.execute(
            "UPDATE jobs SET lease_expires_at=now()+interval '30 minutes',last_heartbeat=now(),updated_at=now() WHERE id=%s AND status='RUNNING'",
            (job_id,),
        )
        if message_id:
            connection.execute(
                "UPDATE inbox_messages SET lease_expires_at=now()+interval '30 minutes',worker_id=%s WHERE message_id=%s",
                (socket.gethostname(), message_id),
            )


def update_asset(media_asset_id: str, sha256: str, archive_key: str, preview_key: str, asr_key: str, duration_ms: int) -> None:
    with psycopg.connect(_conninfo()) as connection:
        connection.execute(
            "UPDATE media_assets SET sha256=CASE WHEN duplicate_of IS NULL THEN %s ELSE NULL END,archive_storage_key=%s,preview_storage_key=%s,asr_storage_key=%s,duration_ms=%s,status='READY' WHERE id=%s AND status <> 'READY' AND EXISTS(SELECT 1 FROM jobs WHERE media_asset_id=media_assets.id AND status <> 'CANCELLED')",
            (sha256, archive_key, preview_key, asr_key, duration_ms, media_asset_id),
        )


def update_recording_session_state(session_id: str, state: str) -> None:
    """Expose media lifecycle independently from the transcription job."""
    with psycopg.connect(_conninfo()) as connection:
        connection.execute(
            "UPDATE recording_sessions SET state=%s WHERE id=%s",
            (state, session_id),
        )


def record_stage_timing(session_id: str, stage: str, elapsed_ms: int) -> None:
    """Persist numeric timing only; no transcript or request data enters diagnostics."""
    with psycopg.connect(_conninfo()) as connection:
        connection.execute(
            "UPDATE recording_sessions SET stage_timings=jsonb_set(COALESCE(stage_timings,'{}'::jsonb), ARRAY[%s], to_jsonb(%s::bigint), true) WHERE id=%s",
            (stage, max(0, int(elapsed_ms)), session_id),
        )
