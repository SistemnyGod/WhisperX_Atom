from __future__ import annotations

import json
import os
import socket
from typing import Any

import psycopg
from psycopg.types.json import Jsonb
from diarization_quality import normalize_speaker_label

ASR_JOB_TYPES = ("TRANSCRIBE", "TRANSCRIBE_ASR", "TRANSCRIBE_REPROCESS")
ENRICHMENT_JOB_TYPE = "TRANSCRIPT_ENRICH"


class JobRepository:
    def __init__(self) -> None:
        self._conninfo = os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")

    @property
    def conninfo(self) -> str:
        return self._conninfo

    def reset_stale_leases(self) -> int:
        """Requeue ASR jobs abandoned by a crashed/restarted GPU worker.

        Inbox leases are deliberately long enough for model inference, but a
        process restart must not make the durable NATS message invisible for
        the full lease interval.  A fresh job heartbeat is the ownership
        signal; stale RUNNING jobs and their inbox rows are safe to reclaim.
        """
        stale_seconds = max(30, int(os.getenv("GPU_STALE_LEASE_SECONDS", "90")))
        with psycopg.connect(self.conninfo) as connection:
            with connection.transaction():
                connection.execute(
                    f"""
                    UPDATE inbox_messages AS inbox
                    SET lease_expires_at=now() - interval '1 second', worker_id=NULL
                    FROM jobs AS job
                    WHERE inbox.job_id=job.id
                      AND job.type IN ('TRANSCRIBE','TRANSCRIBE_ASR','TRANSCRIBE_REPROCESS','TRANSCRIPT_ENRICH')
                      AND job.status IN ('QUEUED','RUNNING')
                      AND (
                          job.status='QUEUED'
                          OR job.last_heartbeat IS NULL
                          OR job.last_heartbeat < now() - interval '{stale_seconds} seconds'
                          OR job.lease_expires_at IS NULL
                          OR job.lease_expires_at < now()
                      )
                    """
                )
                changed = connection.execute(
                    f"""
                    UPDATE jobs
                    SET status='QUEUED',
                        stage=CASE
                            WHEN type IN ('TRANSCRIBE','TRANSCRIBE_ASR','TRANSCRIBE_REPROCESS') THEN 'READY_FOR_ASR'
                            WHEN type='TRANSCRIPT_ENRICH' THEN 'TRANSCRIPT_ENRICH'
                            ELSE stage
                        END,
                        worker_id=NULL,
                        lease_expires_at=NULL,
                        last_heartbeat=NULL,
                        error_code=COALESCE(error_code,'WORKER_RESTART_RECOVERY'),
                        updated_at=now()
                    WHERE type IN ('TRANSCRIBE','TRANSCRIBE_ASR','TRANSCRIBE_REPROCESS','TRANSCRIPT_ENRICH')
                      AND status='RUNNING'
                      AND (
                          last_heartbeat IS NULL
                          OR last_heartbeat < now() - interval '{stale_seconds} seconds'
                          OR lease_expires_at IS NULL
                          OR lease_expires_at < now()
                      )
                    """
                )
                return int(changed.rowcount)

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
                "UPDATE jobs SET status=%s, stage=%s, progress=%s, error_message=%s,error_code=%s,worker_id=%s,lease_expires_at=CASE WHEN %s IN ('READY','FAILED','CANCELLED') THEN NULL ELSE now()+interval '30 minutes' END,last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'",
                (status, stage, progress, error, error_code, socket.gethostname(), status, job_id),
            )
            if status == "FAILED":
                connection.execute(
                    """
                    UPDATE meetings AS meeting
                    SET status='FAILED'
                    FROM jobs AS job
                    WHERE job.id=%s
                      AND job.meeting_id=meeting.id
                      AND meeting.status IN ('INGESTING','MEDIA_PROCESSING','TRANSCRIBING','ALIGNING','DIARIZING')
                      AND NOT EXISTS (
                          SELECT 1 FROM transcripts AS transcript
                          WHERE transcript.meeting_id=meeting.id
                            AND transcript.status IN ('READY','PARTIAL_READY')
                      )
                    """,
                    (job_id,),
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

    def resolve_pipeline_correlation(self, job_id: str, meeting_id: str) -> str | None:
        with psycopg.connect(self.conninfo) as connection:
            row = connection.execute("SELECT pipeline_correlation_id FROM jobs WHERE id=%s", (job_id,)).fetchone()
            if row and row[0]:
                return str(row[0])
            row = connection.execute(
                "SELECT pipeline_correlation_id FROM recording_sessions WHERE meeting_id=%s ORDER BY created_at DESC LIMIT 1",
                (meeting_id,),
            ).fetchone()
            return str(row[0]) if row and row[0] else None

    def persist_asr_draft(self, job_id: str, meeting_id: str, draft: dict[str, Any]) -> str:
        """Persist the ASR-only Transcript V1 before alignment/diarization.

        The job id is embedded in quality metadata as an idempotency key. A
        worker retry therefore returns the same draft instead of creating a
        second V1. Enrichment later creates V2 and never mutates this row.
        """
        with psycopg.connect(self.conninfo) as connection:
            with connection.transaction():
                meeting = connection.execute("SELECT status FROM meetings WHERE id=%s FOR UPDATE", (meeting_id,)).fetchone()
                if meeting is None or str(meeting[0]) == "CANCELLED":
                    raise RuntimeError("MEETING_CANCELLED")
                job = connection.execute("SELECT status FROM jobs WHERE id=%s FOR UPDATE", (job_id,)).fetchone()
                if job is None or str(job[0]) == "CANCELLED":
                    raise RuntimeError("TRANSCRIBE_JOB_CANCELLED")
                existing = connection.execute(
                    "SELECT id FROM transcripts WHERE meeting_id=%s AND version_kind='ASR_DRAFT' AND quality_metadata->>'processing_job_id'=%s ORDER BY version DESC LIMIT 1",
                    (meeting_id, job_id),
                ).fetchone()
                if existing:
                    return str(existing[0])
                version_row = connection.execute("SELECT COALESCE(MAX(version),0)+1 FROM transcripts WHERE meeting_id=%s", (meeting_id,)).fetchone()
                version = int(version_row[0])
                quality = dict(draft.get("quality") or {})
                quality["processing_job_id"] = job_id
                warnings = list(draft.get("warnings") or [])
                result_error_code = str(draft.get("error_code") or "").strip().upper() or None
                transcript_id = connection.execute(
                    "INSERT INTO transcripts(id,meeting_id,version,status,language,model_name,warnings,quality_metadata,quality_score,processing_profile,selected_asr_pass,source_transcript_id,version_kind) VALUES(gen_random_uuid(),%s,%s,'PARTIAL_READY',%s,%s,%s::jsonb,%s::jsonb,%s,%s,%s,NULL,'ASR_DRAFT') RETURNING id",
                    (meeting_id, version, draft.get("language"), (draft.get("metadata") or {}).get("model"), json.dumps(warnings), json.dumps(quality), quality.get("quality_score"), (draft.get("metadata") or {}).get("processing_profile"), (draft.get("metadata") or {}).get("selected_asr_pass")),
                ).fetchone()[0]
                for ordinal, segment in enumerate(draft.get("segments", [])):
                    connection.execute(
                        "INSERT INTO transcript_segments(id,transcript_id,ordinal,start_ms,end_ms,speaker_id,speaker_label,text,confidence,words) VALUES(gen_random_uuid(),%s,%s,%s,%s,NULL,'UNKNOWN',%s,%s,%s)",
                        (transcript_id, ordinal, int(float(segment.get("start", 0)) * 1000), int(float(segment.get("end", 0)) * 1000), str(segment.get("text", "")).strip(), segment.get("confidence"), Jsonb(segment.get("words", []))),
                    )
                connection.execute(
                    "UPDATE jobs SET stage='ASR_READY',progress=45,error_code=%s,last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'",
                    (result_error_code, job_id),
                )
                return str(transcript_id)

    def persist_result(self, job_id: str, meeting_id: str, result: dict[str, Any]) -> bool:
        with psycopg.connect(self.conninfo) as connection:
            # Serialize transcript versions and summary-job creation per meeting.
            meeting = connection.execute("SELECT status FROM meetings WHERE id=%s FOR UPDATE", (meeting_id,)).fetchone()
            if meeting is None or str(meeting[0]) == "CANCELLED":
                return False
            job = connection.execute("SELECT status,type,pipeline_correlation_id FROM jobs WHERE id=%s FOR UPDATE", (job_id,)).fetchone()
            if job is None or str(job[0]) == "CANCELLED":
                return False
            correlation_id = result.get("correlation_id") or (str(job[2]) if job[2] else None)
            if not correlation_id:
                fallback = connection.execute(
                    "SELECT pipeline_correlation_id FROM recording_sessions WHERE meeting_id=%s ORDER BY created_at DESC LIMIT 1",
                    (meeting_id,),
                ).fetchone()
                correlation_id = str(fallback[0]) if fallback and fallback[0] else None
            if correlation_id:
                result["correlation_id"] = correlation_id
                connection.execute("UPDATE jobs SET pipeline_correlation_id=%s WHERE id=%s", (correlation_id, job_id))
            existing = connection.execute("SELECT id, version FROM transcripts WHERE meeting_id=%s ORDER BY version DESC LIMIT 1", (meeting_id,)).fetchone()
            version = int(existing[1]) + 1 if existing else 1
            source_transcript_id = result.get("source_transcript_id") or (existing[0] if existing else None)
            # Legacy reprocessing keeps version_kind = "REPROCESSED";
            # enrichment jobs explicitly override it with "ENRICHED".
            version_kind = str(result.get("version_kind") or ("REPROCESSED" if str(job[1]) == "TRANSCRIBE_REPROCESS" else "GENERATED"))
            transcript_status = result.get("status", "READY")
            result_error_code = str(result.get("error_code") or "").strip().upper() or None
            warnings = result.get("warnings") or []
            quality = result.get("quality", {})
            no_speech_detected = result_error_code == "NO_SPEECH_DETECTED" or "NO_SPEECH_DETECTED" in {str(item).upper() for item in warnings}
            if no_speech_detected and result_error_code is None:
                result_error_code = "NO_SPEECH_DETECTED"
            transcript_id = connection.execute(
                "INSERT INTO transcripts(id,meeting_id,version,status,language,model_name,warnings,quality_metadata,quality_score,processing_profile,selected_asr_pass,source_transcript_id,version_kind) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s::jsonb,%s::jsonb,%s,%s,%s,%s,%s) RETURNING id",
                (meeting_id, version, transcript_status, result.get("language"), result.get("metadata", {}).get("model"), json.dumps(warnings), json.dumps(quality), quality.get("quality_score"), result.get("metadata", {}).get("processing_profile"), quality.get("selected_pass"), source_transcript_id, version_kind),
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
            if os.getenv("AUTO_SUMMARY_ENABLED", "false").lower() in {"1", "true", "yes"} and not no_speech_detected:
                summary_profile = os.getenv("AUTO_SUMMARY_PROFILE", "MEETING_PROTOCOL_RU").strip().upper()
                prompt_version = os.getenv("AUTO_SUMMARY_PROMPT_VERSION", "meeting-protocol-ru-v1")
                summary_job = connection.execute(
                    "SELECT id FROM jobs WHERE input_transcript_id=%s AND type='SUMMARIZE' AND status IN ('QUEUED','RUNNING') LIMIT 1",
                    (transcript_id,),
                ).fetchone()
                if summary_job is None:
                    summary_job_id = connection.execute(
                        "INSERT INTO jobs(id,meeting_id,type,status,stage,progress,input_transcript_id,pipeline_correlation_id) VALUES(gen_random_uuid(),%s,'SUMMARIZE','QUEUED','TRANSCRIPT_READY',0,%s,%s) ON CONFLICT DO NOTHING RETURNING id",
                        (meeting_id, transcript_id, correlation_id),
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
                connection.execute(
                    "UPDATE meetings SET status=%s WHERE id=%s",
                    ("PARTIAL_READY" if no_speech_detected else "TRANSCRIPT_READY", meeting_id),
                )
            result_error_message = "Речь не обнаружена в корректном аудиофайле." if result_error_code == "NO_SPEECH_DETECTED" else None
            final_stage = "ENRICHED_READY" if version_kind == "ENRICHED" else "ASR_READY"
            connection.execute("UPDATE jobs SET status='READY',stage=%s,progress=100,error_message=%s,error_code=%s,lease_expires_at=NULL,last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'", (final_stage, result_error_message, result_error_code, job_id))
            return True
