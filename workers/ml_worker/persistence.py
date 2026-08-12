from __future__ import annotations

import json
import os
import socket
from typing import Any

import psycopg
from psycopg.types.json import Jsonb
from diarization_quality import normalize_speaker_label


class JobRepository:
    def __init__(self) -> None:
        self._conninfo = os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")

    @property
    def conninfo(self) -> str:
        return self._conninfo

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
                "UPDATE jobs SET status=%s, stage=%s, progress=%s, error_message=%s,error_code=%s,worker_id=%s,lease_expires_at=now()+interval '30 minutes',last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'",
                (status, stage, progress, error, error_code, socket.gethostname(), job_id),
            )

    def renew_lease(self, job_id: str, message_id: str | None = None) -> None:
        with psycopg.connect(self.conninfo) as connection:
            connection.execute(
                "UPDATE jobs SET lease_expires_at=now()+interval '30 minutes',last_heartbeat=now(),updated_at=now() WHERE id=%s AND status='RUNNING'",
                (job_id,),
            )
            if message_id:
                connection.execute(
                    "UPDATE inbox_messages SET lease_expires_at=now()+interval '30 minutes',worker_id=%s WHERE message_id=%s",
                    (socket.gethostname(), message_id),
                )

    def persist_result(self, job_id: str, meeting_id: str, result: dict[str, Any]) -> bool:
        with psycopg.connect(self.conninfo) as connection:
            # Serialize transcript versions and summary-job creation per meeting.
            meeting = connection.execute("SELECT status FROM meetings WHERE id=%s FOR UPDATE", (meeting_id,)).fetchone()
            if meeting is None or str(meeting[0]) == "CANCELLED":
                return False
            job = connection.execute("SELECT status,type FROM jobs WHERE id=%s FOR UPDATE", (job_id,)).fetchone()
            if job is None or str(job[0]) == "CANCELLED":
                return False
            existing = connection.execute("SELECT id, version FROM transcripts WHERE meeting_id=%s ORDER BY version DESC LIMIT 1", (meeting_id,)).fetchone()
            version = int(existing[1]) + 1 if existing else 1
            version_kind = "REPROCESSED" if str(job[1]) == "TRANSCRIBE_REPROCESS" else "GENERATED"
            transcript_status = result.get("status", "READY")
            warnings = result.get("warnings", [])
            quality = result.get("quality", {})
            transcript_id = connection.execute(
                "INSERT INTO transcripts(id,meeting_id,version,status,language,model_name,warnings,quality_metadata,quality_score,processing_profile,selected_asr_pass,source_transcript_id,version_kind) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s::jsonb,%s::jsonb,%s,%s,%s,%s,%s) RETURNING id",
                (meeting_id, version, transcript_status, result.get("language"), result.get("metadata", {}).get("model"), json.dumps(warnings), json.dumps(quality), quality.get("quality_score"), result.get("metadata", {}).get("processing_profile"), quality.get("selected_pass"), existing[0] if existing else None, version_kind),
            ).fetchone()[0]
            speakers: dict[str, str] = {}
            for segment in result.get("segments", []):
                label = normalize_speaker_label(segment.get("speaker"))
                if not label or label in speakers:
                    continue
                speaker_id = connection.execute(
                    "INSERT INTO meeting_speakers(id,meeting_id,stable_key,display_name,confidence) VALUES(gen_random_uuid(),%s,%s,%s,NULL) ON CONFLICT(meeting_id,stable_key) DO UPDATE SET stable_key=excluded.stable_key RETURNING id",
                    (meeting_id, label, label.replace("SPEAKER_", "Спикер ")),
                ).fetchone()[0]
                speakers[label] = str(speaker_id)
            technical_events = connection.execute(
                "SELECT e.event_type,e.media_time_ms FROM recording_events e JOIN recording_sessions rs ON rs.id=e.session_id WHERE rs.meeting_id=%s AND e.media_time_ms IS NOT NULL",
                (meeting_id,),
            ).fetchall()
            technical_events = [(str(event_type).upper(), int(media_time_ms)) for event_type, media_time_ms in technical_events]
            for ordinal, segment in enumerate(result.get("segments", [])):
                label = normalize_speaker_label(segment.get("speaker"))
                connection.execute(
                    "INSERT INTO transcript_segments(id,transcript_id,ordinal,start_ms,end_ms,speaker_id,speaker_label,text,confidence,words,segment_kind,is_hidden) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s) ON CONFLICT(transcript_id,ordinal) DO UPDATE SET text=excluded.text,end_ms=excluded.end_ms,speaker_id=excluded.speaker_id,speaker_label=excluded.speaker_label,confidence=excluded.confidence,words=excluded.words,segment_kind=excluded.segment_kind,is_hidden=excluded.is_hidden",
                    (transcript_id, ordinal, int(float(segment.get("start", 0)) * 1000), int(float(segment.get("end", 0)) * 1000), speakers.get(label) if label else None, label or "UNKNOWN", str(segment.get("text", "")).strip(), segment.get("confidence"), Jsonb(segment.get("words", [])), next((event_type for event_type, event_time in technical_events if int(float(segment.get("start", 0)) * 1000) <= event_time <= int(float(segment.get("end", 0)) * 1000)), str(segment.get("segment_kind", "SPEECH"))), bool(segment.get("is_hidden", False)) or any(event_type in {"VOICE_COMMAND", "SYSTEM_RESPONSE"} and int(float(segment.get("start", 0)) * 1000) <= event_time <= int(float(segment.get("end", 0)) * 1000) for event_type, event_time in technical_events)),
                )
            if os.getenv("AUTO_SUMMARY_ENABLED", "false").lower() in {"1", "true", "yes"}:
                summary_profile = os.getenv("AUTO_SUMMARY_PROFILE", "MEETING_PROTOCOL_RU").strip().upper()
                prompt_version = os.getenv("AUTO_SUMMARY_PROMPT_VERSION", "meeting-protocol-ru-v1")
                summary_job = connection.execute(
                    "SELECT id FROM jobs WHERE input_transcript_id=%s AND type='SUMMARIZE' AND status IN ('QUEUED','RUNNING') LIMIT 1",
                    (transcript_id,),
                ).fetchone()
                if summary_job is None:
                    summary_job_id = connection.execute(
                        "INSERT INTO jobs(id,meeting_id,type,status,stage,progress,input_transcript_id) VALUES(gen_random_uuid(),%s,'SUMMARIZE','QUEUED','TRANSCRIPT_READY',0,%s) ON CONFLICT DO NOTHING RETURNING id",
                        (meeting_id, transcript_id),
                    ).fetchone()
                    if summary_job_id is None:
                        connection.execute("UPDATE meetings SET status='SUMMARIZING' WHERE id=%s", (meeting_id,))
                    else:
                        summary_job_id = summary_job_id[0]
                        message_id = connection.execute("SELECT gen_random_uuid()").fetchone()[0]
                        payload = json.dumps({
                            "message_id": str(message_id),
                            "job_id": str(summary_job_id),
                            "meeting_id": meeting_id,
                            "transcript_id": str(transcript_id),
                            "source_hash": result.get("source_hash"),
                            "summary_profile": summary_profile,
                            "prompt_version": prompt_version,
                            "meeting_context": {},
                            "correlation_id": result.get("correlation_id"),
                        })
                        connection.execute("INSERT INTO outbox_messages(id,topic,payload) VALUES(%s,'llm.summarize',%s::jsonb)", (message_id, payload))
                connection.execute("UPDATE meetings SET status='SUMMARIZING' WHERE id=%s", (meeting_id,))
            else:
                connection.execute("UPDATE meetings SET status='TRANSCRIPT_READY' WHERE id=%s", (meeting_id,))
            connection.execute("UPDATE jobs SET status='READY',stage='TRANSCRIPT_READY',progress=100,error_message=NULL,error_code=NULL,lease_expires_at=NULL,last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'", (job_id,))
            return True
