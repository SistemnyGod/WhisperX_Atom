from __future__ import annotations

import os
import socket
from typing import Any

import psycopg
from psycopg.types.json import Jsonb


class JobRepository:
    def __init__(self) -> None:
        self.conninfo = os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")

    def claim_message(self, message_id: str | None, job_id: str | None = None) -> bool:
        if not message_id:
            return True
        with psycopg.connect(self.conninfo) as connection:
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

    def job_state(self, job_id: str | None) -> tuple[str, str] | None:
        if not job_id:
            return None
        with psycopg.connect(self.conninfo) as connection:
            return connection.execute("SELECT status, stage FROM jobs WHERE id=%s", (job_id,)).fetchone()

    def release_message(self, message_id: str | None) -> None:
        if not message_id:
            return
        with psycopg.connect(self.conninfo) as connection:
            connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (message_id,))

    def update_job(self, job_id: str, status: str, stage: str, progress: int, error: str | None = None, error_code: str | None = None) -> None:
        with psycopg.connect(self.conninfo) as connection:
            connection.execute(
                "UPDATE jobs SET status=%s, stage=%s, progress=%s, error_message=%s,error_code=%s,worker_id=%s,lease_expires_at=now()+interval '30 minutes',last_heartbeat=now(),updated_at=now() WHERE id=%s",
                (status, stage, progress, error, error_code, socket.gethostname(), job_id),
            )

    def persist_result(self, job_id: str, meeting_id: str, result: dict[str, Any]) -> None:
        with psycopg.connect(self.conninfo) as connection:
            existing = connection.execute("SELECT id, version FROM transcripts WHERE meeting_id=%s ORDER BY version DESC LIMIT 1", (meeting_id,)).fetchone()
            version = int(existing[1]) + 1 if existing else 1
            transcript_id = connection.execute(
                "INSERT INTO transcripts(id,meeting_id,version,status,language,model_name) VALUES(gen_random_uuid(),%s,%s,'READY',%s,%s) RETURNING id",
                (meeting_id, version, result.get("language"), result.get("metadata", {}).get("model")),
            ).fetchone()[0]
            speakers: dict[str, str] = {}
            for segment in result.get("segments", []):
                label = segment.get("speaker")
                if not label or label in speakers:
                    continue
                speaker_id = connection.execute(
                    "INSERT INTO meeting_speakers(id,meeting_id,stable_key,display_name,confidence) VALUES(gen_random_uuid(),%s,%s,%s,NULL) ON CONFLICT(meeting_id,stable_key) DO UPDATE SET stable_key=excluded.stable_key RETURNING id",
                    (meeting_id, label, label.replace("SPEAKER_", "Спикер ")),
                ).fetchone()[0]
                speakers[label] = str(speaker_id)
            for ordinal, segment in enumerate(result.get("segments", [])):
                label = segment.get("speaker")
                connection.execute(
                    "INSERT INTO transcript_segments(id,transcript_id,ordinal,start_ms,end_ms,speaker_id,speaker_label,text,confidence,words) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s,%s,%s,%s) ON CONFLICT(transcript_id,ordinal) DO UPDATE SET text=excluded.text,end_ms=excluded.end_ms,speaker_id=excluded.speaker_id,speaker_label=excluded.speaker_label,confidence=excluded.confidence,words=excluded.words",
                    (transcript_id, ordinal, int(float(segment.get("start", 0)) * 1000), int(float(segment.get("end", 0)) * 1000), speakers.get(label) if label else None, label, str(segment.get("text", "")).strip(), segment.get("confidence"), Jsonb(segment.get("words", []))),
                )
            connection.execute("UPDATE jobs SET status='READY',stage='READY',progress=100,error_message=NULL,error_code=NULL,lease_expires_at=NULL,last_heartbeat=now(),updated_at=now() WHERE id=%s", (job_id,))
            connection.execute("UPDATE meetings SET status='READY' WHERE id=%s", (meeting_id,))
