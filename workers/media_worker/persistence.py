from __future__ import annotations

import os
import socket

import psycopg


def _conninfo() -> str:
    return os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")


def claim_message(message_id: str | None) -> bool:
    if not message_id:
        return True
    with psycopg.connect(_conninfo()) as connection:
        row = connection.execute("INSERT INTO inbox_messages(message_id) VALUES(%s) ON CONFLICT DO NOTHING RETURNING message_id", (message_id,)).fetchone()
        return row is not None


def release_message(message_id: str | None) -> None:
    if not message_id:
        return
    with psycopg.connect(_conninfo()) as connection:
        connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (message_id,))


def update_job(job_id: str, status: str, stage: str, progress: int, error: str | None = None, error_code: str | None = None) -> None:
    with psycopg.connect(_conninfo()) as connection:
        connection.execute(
            "UPDATE jobs SET status=%s,stage=%s,progress=%s,error_message=%s,error_code=%s,worker_id=%s,lease_expires_at=now()+interval '30 minutes',last_heartbeat=now(),updated_at=now() WHERE id=%s",
            (status, stage, progress, error, error_code, socket.gethostname(), job_id),
        )


def update_asset(media_asset_id: str, sha256: str, archive_key: str, preview_key: str, asr_key: str, duration_ms: int) -> None:
    with psycopg.connect(_conninfo()) as connection:
        connection.execute(
            "UPDATE media_assets SET sha256=%s,archive_storage_key=%s,preview_storage_key=%s,asr_storage_key=%s,duration_ms=%s,status='READY' WHERE id=%s",
            (sha256, archive_key, preview_key, asr_key, duration_ms, media_asset_id),
        )
