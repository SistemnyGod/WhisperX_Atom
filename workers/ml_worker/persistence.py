from __future__ import annotations

import json
import os
import socket
from dataclasses import dataclass
from datetime import datetime
from typing import Any

import psycopg
from psycopg.types.json import Jsonb
from workers.db_pool import DatabaseConnectionPool
from diarization_quality import normalize_speaker_label
from .speaker_registry import default_display_label, match_profile, normalize_embedding
from whisperx_atom.pipeline_contract import validate_stage_name
from .technical_events import build_technical_intervals, segment_technical_flags

ASR_JOB_TYPES = ("TRANSCRIBE", "TRANSCRIBE_ASR", "TRANSCRIBE_REPROCESS")
ENRICHMENT_JOB_TYPE = "TRANSCRIPT_ENRICH"


@dataclass(frozen=True)
class MessageClaimResult:
    """Durable inbox ownership outcome used by the GPU consumer.

    A boolean cannot distinguish a terminal redelivery from a live owner.  In
    particular, treating ``OWNED_BY_OTHER_WORKER`` as an exception creates a
    NATS hot loop.  The explicit outcome is intentionally small so older
    callers can keep using :meth:`claim_message` below.
    """

    status: str
    owner: str | None = None
    lease_expires_at: datetime | None = None

    @property
    def acquired(self) -> bool:
        return self.status in {"ACQUIRED", "OWNED_BY_THIS_WORKER"}


def _speaker_embedding(result: dict[str, Any], label: str, segments: list[dict[str, Any]]) -> Any:
    """Read an embedding only when the diarizer explicitly supplied one."""
    embeddings = result.get("speaker_embeddings")
    if isinstance(embeddings, dict) and label in embeddings:
        return embeddings[label]
    for segment in segments:
        if normalize_speaker_label(segment.get("speaker")) != label:
            continue
        for key in ("speaker_embedding", "embedding"):
            if segment.get(key) is not None:
                return segment.get(key)
    return None


def _technical_intervals(connection: psycopg.Connection[Any], meeting_id: str) -> list[tuple[int, int, str]]:
    rows = connection.execute(
        "SELECT e.event_type,e.media_time_ms,e.payload FROM recording_events e "
        "JOIN recording_sessions rs ON rs.id=e.session_id "
        "WHERE rs.meeting_id=%s AND e.media_time_ms IS NOT NULL "
        "ORDER BY e.media_time_ms,e.created_at",
        (meeting_id,),
    ).fetchall()
    return build_technical_intervals(rows)


class JobRepository:
    def __init__(self) -> None:
        self._conninfo = os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")
        self._db = DatabaseConnectionPool(self._conninfo, "ml-worker")

    @property
    def conninfo(self) -> str:
        return self._conninfo

    def close(self) -> None:
        self._db.close()

    def reset_stale_leases(self) -> int:
        """Requeue ASR jobs abandoned by a crashed/restarted GPU worker.

        Inbox leases are deliberately long enough for model inference, but a
        process restart must not make the durable NATS message invisible for
        the full lease interval.  A fresh job heartbeat is the ownership
        signal; stale RUNNING jobs and their inbox rows are safe to reclaim.
        """
        stale_seconds = max(30, int(os.getenv("GPU_STALE_LEASE_SECONDS", "90")))
        with self._db.connection() as connection:
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
                        error_code='WORKER_RESTART_RECOVERY',
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

    def claim_message_details(self, message_id: str | None, job_id: str | None = None) -> MessageClaimResult:
        if not message_id:
            return MessageClaimResult("ACQUIRED")
        worker_id = socket.gethostname()
        with self._db.connection() as connection:
            with connection.transaction():
                existing = connection.execute(
                    "SELECT job_id,worker_id,lease_expires_at FROM inbox_messages WHERE message_id=%s FOR UPDATE",
                    (message_id,),
                ).fetchone()
                if existing is None:
                    row = connection.execute(
                        """INSERT INTO inbox_messages(message_id,job_id,lease_expires_at,worker_id)
                           VALUES(%s,%s,now()+interval '30 minutes',%s)
                           RETURNING worker_id,lease_expires_at""",
                        (message_id, job_id, worker_id),
                    ).fetchone()
                    return MessageClaimResult("ACQUIRED", str(row[0]) if row and row[0] else worker_id, row[1] if row else None)
                existing_job_id, owner, lease_expires_at = existing
                terminal = connection.execute(
                    "SELECT status FROM jobs WHERE id=%s",
                    (job_id or existing_job_id,),
                ).fetchone()
                if terminal is not None and str(terminal[0]) in {"READY", "FAILED", "CANCELLED"}:
                    return MessageClaimResult("TERMINAL", str(owner) if owner else None, lease_expires_at)
                lease_expired = (
                    lease_expires_at is None
                    or lease_expires_at.timestamp() <= datetime.now().timestamp()
                )
                if lease_expired or str(owner or "") == worker_id:
                    # A serial GPU worker has no concurrent handler.  A lease
                    # owned by this hostname is therefore an orphan from a
                    # previous process and may be reclaimed atomically.
                    row = connection.execute(
                        """UPDATE inbox_messages
                           SET job_id=%s,lease_expires_at=now()+interval '30 minutes',worker_id=%s
                           WHERE message_id=%s
                           RETURNING worker_id,lease_expires_at""",
                        (job_id, worker_id, message_id),
                    ).fetchone()
                    return MessageClaimResult("OWNED_BY_THIS_WORKER" if not lease_expired else "ACQUIRED", worker_id, row[1] if row else None)
                return MessageClaimResult("OWNED_BY_OTHER_WORKER", str(owner) if owner else None, lease_expires_at)

    def claim_message(self, message_id: str | None, job_id: str | None = None) -> bool:
        """Compatibility wrapper for media/legacy workers."""
        return self.claim_message_details(message_id, job_id).acquired

    def job_state(self, job_id: str | None) -> tuple[str, str] | None:
        if not job_id:
            return None
        with self._db.connection() as connection:
            return connection.execute("SELECT status, stage FROM jobs WHERE id=%s", (job_id,)).fetchone()

    def release_message(self, message_id: str | None) -> None:
        if not message_id:
            return
        with self._db.connection() as connection:
            connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (message_id,))

    def schedule_retry(self, job_id: str, error: str, error_code: str, message_id: str | None = None, max_attempts: int = 3) -> int | None:
        """Return the new attempt number when a transient retry was claimed.

        The job is made visible to the outbox/NATS retry path only while its
        attempt budget remains.  A terminal FAILED job must never be NAKed as
        if it could be retried: JetStream redelivery would otherwise be
        acknowledged by the terminal-state guard without doing any work.
        """
        with self._db.connection() as connection:
            with connection.transaction():
                row = connection.execute(
                    """
                    UPDATE jobs
                    SET status='QUEUED', stage='RETRY_PENDING', progress=0,
                        attempt=attempt+1, error_message=%s, error_code=%s,
                        worker_id=NULL, lease_expires_at=NULL,
                        last_heartbeat=now(), updated_at=now()
                    WHERE id=%s AND status NOT IN ('CANCELLED','READY','FAILED')
                      AND attempt < %s
                    RETURNING attempt
                    """,
                    (error, error_code, job_id, max_attempts),
                ).fetchone()
                if row is not None and message_id:
                    connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (message_id,))
            return int(row[0]) if row else None

    def _mark_terminal_failure_locked(
        self,
        connection: psycopg.Connection[Any],
        job_id: str,
        error: str,
        error_code: str,
    ) -> None:
        """Persist a terminal failure and synchronize the meeting state.

        This method must be called while the caller owns the current job row
        lock.  It is shared by normal exceptions and watchdog terminal paths
        so enrichment cannot accidentally hide a durable V1 transcript.
        """
        job_type = connection.execute("SELECT type,meeting_id FROM jobs WHERE id=%s FOR UPDATE", (job_id,)).fetchone()
        if job_type is None:
            return
        connection.execute(
            """UPDATE jobs SET status='FAILED',stage='FAILED',progress=0,
                error_message=%s,error_code=%s,worker_id=NULL,lease_expires_at=NULL,
                last_heartbeat=NULL,updated_at=now() WHERE id=%s AND status NOT IN ('READY','FAILED','CANCELLED')""",
            (error, error_code, job_id),
        )
        meeting_id = job_type[1]
        if str(job_type[0]) == ENRICHMENT_JOB_TYPE:
            connection.execute(
                """UPDATE meetings SET status='PARTIAL_READY' WHERE id=%s
                   AND EXISTS (SELECT 1 FROM transcripts WHERE meeting_id=%s
                               AND version_kind='ASR_DRAFT' AND status IN ('READY','PARTIAL_READY'))""",
                (meeting_id, meeting_id),
            )
            return
        connection.execute(
            """UPDATE meetings AS meeting SET status='FAILED'
               FROM jobs AS job WHERE job.id=%s AND job.meeting_id=meeting.id
               AND meeting.status IN ('INGESTING','MEDIA_PROCESSING','TRANSCRIBING','ALIGNING','DIARIZING')
               AND NOT EXISTS (SELECT 1 FROM transcripts AS transcript
                               WHERE transcript.meeting_id=meeting.id
                                 AND transcript.status IN ('READY','PARTIAL_READY'))""",
            (job_id,),
        )

    def update_job(self, job_id: str, status: str, stage: str, progress: int, error: str | None = None, error_code: str | None = None) -> None:
        # Keep the persisted spelling backwards compatible, but reject a
        # stage that is not part of the shared pipeline contract.
        stage = validate_stage_name(stage)
        with self._db.connection() as connection:
            if status == "FAILED":
                with connection.transaction():
                    self._mark_terminal_failure_locked(connection, job_id, error or "job failed", error_code or "JOB_FAILED")
                return
            job_type = connection.execute("SELECT type,meeting_id FROM jobs WHERE id=%s", (job_id,)).fetchone()
            connection.execute(
                "UPDATE jobs SET status=%s, stage=%s, progress=%s, error_message=%s,error_code=%s,worker_id=%s,lease_expires_at=CASE WHEN %s IN ('READY','FAILED','CANCELLED') THEN NULL ELSE now()+interval '30 minutes' END,last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'",
                (status, stage, progress, error, error_code, socket.gethostname(), status, job_id),
            )

    def renew_lease(self, job_id: str, message_id: str | None = None) -> None:
        with self._db.connection() as connection:
            connection.execute(
                "UPDATE jobs SET lease_expires_at=now()+interval '30 minutes',last_heartbeat=now() WHERE id=%s AND status='RUNNING' AND worker_id=%s",
                (job_id, socket.gethostname()),
            )
            if message_id:
                connection.execute(
                    "UPDATE inbox_messages SET lease_expires_at=now()+interval '30 minutes',worker_id=%s WHERE message_id=%s AND worker_id=%s",
                    (socket.gethostname(), message_id, socket.gethostname()),
                )

    def job_progress_liveness(self, job_id: str) -> tuple[str, int, Any, Any] | None:
        """Return durable stage/progress timestamps without touching updated_at."""
        with self._db.connection() as connection:
            try:
                row = connection.execute(
                    "SELECT stage,progress,stage_changed_at,progress_changed_at FROM jobs WHERE id=%s",
                    (job_id,),
                ).fetchone()
            except Exception:
                # Rolling upgrades may start the worker before migration 039;
                # use the last durable update as a conservative fallback.
                connection.rollback()
                row = connection.execute(
                    "SELECT stage,progress,updated_at,updated_at FROM jobs WHERE id=%s",
                    (job_id,),
                ).fetchone()
        if row is None:
            return None
        return str(row[0]), int(row[1] or 0), row[2], row[3]

    def requeue_or_fail_stage_timeout(self, job_id: str, message_id: str | None = None) -> str | None:
        """Perform the single watchdog recovery permitted for one job.

        The NATS delivery stays unacknowledged; expiring its inbox lease lets
        the restarted worker receive the same durable message. A second
        timeout is terminal, while any persisted V1 remains untouched.
        """
        with self._db.connection() as connection:
            with connection.transaction():
                row = connection.execute(
                    "SELECT status,timeout_requeue_count FROM jobs WHERE id=%s FOR UPDATE",
                    (job_id,),
                ).fetchone()
                if row is None or str(row[0]) != "RUNNING":
                    return None
                timeout_count = int(row[1] or 0)
                if timeout_count < 1:
                    connection.execute(
                        """
                        UPDATE jobs
                        SET status='QUEUED',stage='RETRY_PENDING',progress=0,
                            worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,
                            error_message='GPU stage made no progress within the configured timeout',
                            error_code='GPU_STAGE_TIMEOUT_REQUEUED',
                            timeout_requeue_count=timeout_requeue_count+1,
                            attempt=attempt+1,updated_at=now()
                        WHERE id=%s AND status='RUNNING'
                        """,
                        (job_id,),
                    )
                    if message_id:
                        connection.execute(
                            "UPDATE inbox_messages SET lease_expires_at=now()-interval '1 second',worker_id=NULL WHERE message_id=%s",
                            (message_id,),
                        )
                    return "REQUEUED"
                self._mark_terminal_failure_locked(
                    connection,
                    job_id,
                    'GPU stage exceeded the progress timeout twice',
                    'GPU_STAGE_TIMEOUT',
                )
                if message_id:
                    connection.execute(
                        "UPDATE inbox_messages SET lease_expires_at=now()-interval '1 second',worker_id=NULL WHERE message_id=%s",
                        (message_id,),
                    )
                return "FAILED"

    def resolve_pipeline_correlation(self, job_id: str, meeting_id: str) -> str | None:
        with self._db.connection() as connection:
            row = connection.execute("SELECT pipeline_correlation_id FROM jobs WHERE id=%s", (job_id,)).fetchone()
            if row and row[0]:
                return str(row[0])
            row = connection.execute(
                "SELECT pipeline_correlation_id FROM recording_sessions WHERE meeting_id=%s ORDER BY created_at DESC LIMIT 1",
                (meeting_id,),
            ).fetchone()
            return str(row[0]) if row and row[0] else None

    def record_pipeline_metrics(self, job_id: str, meeting_id: str, metrics: dict[str, Any] | None, prefix: str = "") -> None:
        """Attach numeric GPU-pipeline metrics to the durable recording lineage.

        The result metadata is useful for the worker log, but logs are not a
        reliable per-recording audit trail.  Keep only bounded numeric fields
        in ``recording_sessions.stage_timings``; transcript text, paths and
        credentials never cross this diagnostic boundary.
        """
        if not isinstance(metrics, dict):
            return
        safe: dict[str, float | int | bool | None] = {}
        for key, value in metrics.items():
            if not isinstance(key, str) or len(key) > 80:
                continue
            storage_key = f"{prefix}{key}" if prefix else key
            if isinstance(value, bool) or value is None:
                if key in {"checkpoint_hit"}:
                    safe[storage_key] = value
                continue
            if isinstance(value, (int, float)):
                numeric = float(value)
                if numeric >= 0 and numeric == numeric and numeric != float("inf"):
                    safe[storage_key] = int(numeric) if isinstance(value, int) else round(numeric, 3)
        if not safe:
            return
        payload = Jsonb(safe)
        with self._db.connection() as connection:
            correlation = connection.execute(
                "SELECT pipeline_correlation_id FROM jobs WHERE id=%s",
                (job_id,),
            ).fetchone()
            if correlation and correlation[0]:
                session = connection.execute(
                    "SELECT id FROM recording_sessions WHERE pipeline_correlation_id=%s ORDER BY created_at DESC LIMIT 1",
                    (str(correlation[0]),),
                ).fetchone()
            else:
                session = connection.execute(
                    "SELECT id FROM recording_sessions WHERE meeting_id=%s ORDER BY created_at DESC LIMIT 1",
                    (meeting_id,),
                ).fetchone()
            if not session:
                return
            connection.execute(
                "UPDATE recording_sessions SET stage_timings=COALESCE(stage_timings,'{}'::jsonb) || %s::jsonb WHERE id=%s",
                (payload, session[0]),
            )

    def job_type(self, job_id: str) -> str | None:
        with self._db.connection() as connection:
            row = connection.execute("SELECT type FROM jobs WHERE id=%s", (job_id,)).fetchone()
            return str(row[0]) if row and row[0] else None

    def input_transcript_id(self, job_id: str) -> str | None:
        with self._db.connection() as connection:
            row = connection.execute("SELECT input_transcript_id FROM jobs WHERE id=%s", (job_id,)).fetchone()
            return str(row[0]) if row and row[0] else None

    def technical_intervals(self, meeting_id: str) -> list[tuple[int, int, str]]:
        """Return durable TTS intervals for the derived ASR muting pass."""
        with self._db.connection() as connection:
            return _technical_intervals(connection, meeting_id)

    def load_transcript_source(self, transcript_id: str | None) -> dict[str, Any] | None:
        if not transcript_id:
            return None
        with self._db.connection() as connection:
            transcript = connection.execute(
                "SELECT language,quality_metadata FROM transcripts WHERE id=%s",
                (transcript_id,),
            ).fetchone()
            if transcript is None:
                return None
            rows = connection.execute(
                "SELECT start_ms,end_ms,text,confidence,words FROM transcript_segments WHERE transcript_id=%s AND COALESCE(is_hidden,false)=false ORDER BY ordinal",
                (transcript_id,),
            ).fetchall()
            segments: list[dict[str, Any]] = []
            words: list[dict[str, Any]] = []
            for start_ms, end_ms, text, confidence, segment_words in rows:
                decoded_words = segment_words or []
                segments.append({
                    "start": float(start_ms or 0) / 1000.0,
                    "end": float(end_ms or 0) / 1000.0,
                    "text": str(text or ""),
                    "confidence": confidence,
                    "words": decoded_words,
                    "speaker": "UNKNOWN",
                })
                if isinstance(decoded_words, list):
                    words.extend(decoded_words)
            return {"language": transcript[0], "quality_metadata": transcript[1] or {}, "segments": segments, "word_segments": words, "transcript_id": transcript_id}

    def complete_asr_job(self, job_id: str, meeting_id: str) -> None:
        """Close an ASR-only job without creating a second transcript version."""
        with self._db.connection() as connection:
            with connection.transaction():
                error_row = connection.execute("SELECT error_code FROM jobs WHERE id=%s", (job_id,)).fetchone()
                no_speech = error_row is not None and str(error_row[0] or "").upper() == "NO_SPEECH_DETECTED"
                partial_quality = error_row is not None and str(error_row[0] or "").upper() in {"ASR_LANGUAGE_MISMATCH", "AUDIO_SIGNAL_WEAK", "AUDIO_SIGNAL_UNUSABLE"}
                connection.execute(
                    "UPDATE jobs SET status='READY',stage='ASR_READY',progress=100,error_code=CASE WHEN error_code IN ('NO_SPEECH_DETECTED','ASR_LANGUAGE_MISMATCH','AUDIO_SIGNAL_UNUSABLE','ASR_ENHANCEMENT_FAILED') THEN error_code ELSE NULL END,lease_expires_at=NULL,last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'",
                    (job_id,),
                )
                connection.execute(
                    "UPDATE meetings SET status=%s WHERE id=%s AND status NOT IN ('CANCELLED','FAILED')",
                    ("PARTIAL_READY" if no_speech or partial_quality else "TRANSCRIPT_READY", meeting_id),
                )

    def persist_asr_draft(self, job_id: str, meeting_id: str, draft: dict[str, Any]) -> str:
        """Persist the ASR-only Transcript V1 before alignment/diarization.

        The job id is embedded in quality metadata as an idempotency key. A
        worker retry therefore returns the same draft instead of creating a
        second V1. Enrichment later creates V2 and never mutates this row.
        """
        with self._db.connection() as connection:
            with connection.transaction():
                meeting = connection.execute("SELECT status FROM meetings WHERE id=%s FOR UPDATE", (meeting_id,)).fetchone()
                if meeting is None or str(meeting[0]) == "CANCELLED":
                    raise RuntimeError("MEETING_CANCELLED")
                job = connection.execute("SELECT status,type,pipeline_correlation_id FROM jobs WHERE id=%s FOR UPDATE", (job_id,)).fetchone()
                if job is None or str(job[0]) == "CANCELLED":
                    raise RuntimeError("TRANSCRIBE_JOB_CANCELLED")
                existing = connection.execute(
                    "SELECT id FROM transcripts WHERE meeting_id=%s AND version_kind='ASR_DRAFT' AND quality_metadata->>'processing_job_id'=%s ORDER BY version DESC LIMIT 1",
                    (meeting_id, job_id),
                ).fetchone()
                if existing:
                    # A crash can occur after V1 commit but before the
                    # enrichment job/outbox transaction. Reconcile that
                    # boundary on every retry instead of returning a V1 that
                    # can never reach V2.
                    if str(job[1]) == "TRANSCRIBE_ASR":
                        existing_meta = connection.execute(
                            "SELECT warnings,quality_metadata FROM transcripts WHERE id=%s",
                            (existing[0],),
                        ).fetchone()
                        warning_values = {str(item).upper() for item in (existing_meta[0] or [])} if existing_meta else set()
                        quality_meta = existing_meta[1] if existing_meta and isinstance(existing_meta[1], dict) else {}
                        blocked = warning_values & {"NO_SPEECH_DETECTED", "ASR_LANGUAGE_MISMATCH", "AUDIO_SIGNAL_UNUSABLE"}
                        if not blocked:
                            media = connection.execute(
                                "SELECT a.asr_storage_key,a.duration_ms FROM jobs j JOIN media_assets a ON a.id=j.media_asset_id WHERE j.id=%s",
                                (job_id,),
                            ).fetchone()
                            self._ensure_enrichment_job_and_outbox(
                                connection,
                                meeting_id,
                                str(existing[0]),
                                job_id,
                                str(media[0]) if media and media[0] else None,
                                str(quality_meta.get("language") or "ru"),
                                str(quality_meta.get("acoustic_profile") or "AUTO"),
                                str(job[2]) if job[2] else None,
                                int(media[1]) if media and media[1] else None,
                            )
                    connection.execute("UPDATE recording_pipeline_runs SET transcript_v1_id=%s,updated_at=now() WHERE asr_job_id=%s", (existing[0], job_id))
                    return str(existing[0])
                version_row = connection.execute("SELECT COALESCE(MAX(version),0)+1 FROM transcripts WHERE meeting_id=%s", (meeting_id,)).fetchone()
                version = int(version_row[0])
                quality = dict(draft.get("quality") or {})
                quality["processing_job_id"] = job_id
                metadata = dict(draft.get("metadata") or {})
                for key in ("asr_preprocessing", "asr_storage_key", "asr_sample_rate", "asr_channels", "asr_duration_seconds", "asr_audio_hash", "audio_signal_metrics", "acoustic_profile", "language_quality", "asr_pass_count", "asr_selection_reason", "pipeline_metrics"):
                    if key in metadata:
                        quality[key] = metadata[key]
                warnings = list(draft.get("warnings") or [])
                result_error_code = str(draft.get("error_code") or "").strip().upper() or None
                transcript_id = connection.execute(
                    "INSERT INTO transcripts(id,meeting_id,version,status,language,model_name,warnings,quality_metadata,quality_score,processing_profile,selected_asr_pass,source_transcript_id,version_kind) VALUES(gen_random_uuid(),%s,%s,'PARTIAL_READY',%s,%s,%s::jsonb,%s::jsonb,%s,%s,%s,NULL,'ASR_DRAFT') RETURNING id",
                    (meeting_id, version, draft.get("language"), metadata.get("model"), json.dumps(warnings), json.dumps(quality), quality.get("quality_score"), metadata.get("processing_profile"), metadata.get("selected_asr_pass")),
                ).fetchone()[0]
                connection.execute("UPDATE recording_pipeline_runs SET transcript_v1_id=%s,updated_at=now() WHERE asr_job_id=%s", (transcript_id, job_id))
                technical_intervals = _technical_intervals(connection, meeting_id)
                for ordinal, segment in enumerate(draft.get("segments", [])):
                    start_ms = int(float(segment.get("start", 0)) * 1000)
                    end_ms = int(float(segment.get("end", 0)) * 1000)
                    segment_kind, is_hidden = segment_technical_flags(start_ms, end_ms, technical_intervals)
                    connection.execute(
                        "INSERT INTO transcript_segments(id,transcript_id,ordinal,start_ms,end_ms,speaker_id,speaker_label,text,confidence,words,segment_kind,is_hidden) VALUES(gen_random_uuid(),%s,%s,%s,%s,NULL,'UNKNOWN',%s,%s,%s,%s,%s)",
                        (transcript_id, ordinal, start_ms, end_ms, str(segment.get("text", "")).strip(), segment.get("confidence"), Jsonb(segment.get("words", [])), segment_kind, is_hidden),
                    )
                connection.execute(
                    "UPDATE jobs SET stage='ASR_READY',progress=45,error_code=%s,last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'",
                    (result_error_code, job_id),
                )
                # ASR-only jobs expose V1 immediately and enqueue enrichment
                # separately. The partial transcript is never mutated by V2.
                if str(job[1]) == "TRANSCRIBE_ASR" and result_error_code not in {"NO_SPEECH_DETECTED", "ASR_LANGUAGE_MISMATCH", "AUDIO_SIGNAL_UNUSABLE"}:
                    media = connection.execute(
                        "SELECT a.asr_storage_key,a.duration_ms FROM jobs j JOIN media_assets a ON a.id=j.media_asset_id WHERE j.id=%s",
                        (job_id,),
                    ).fetchone()
                    storage_key = str(media[0]) if media and media[0] else None
                    self._ensure_enrichment_job_and_outbox(
                        connection,
                        meeting_id,
                        str(transcript_id),
                        job_id,
                        storage_key,
                        str(draft.get("language") or quality.get("language") or "ru"),
                        str(quality.get("acoustic_profile") or "AUTO"),
                        str(job[2]) if job[2] else None,
                        int(media[1]) if media and media[1] else None,
                    )
                    connection.execute(
                        "UPDATE meetings SET status=%s WHERE id=%s AND status NOT IN ('CANCELLED','FAILED')",
                        ("PARTIAL_READY" if "AUDIO_SIGNAL_WEAK" in {str(item).upper() for item in warnings} else "TRANSCRIPT_READY", meeting_id),
                    )
                return str(transcript_id)

    @staticmethod
    def _ensure_enrichment_job_and_outbox(
        connection: psycopg.Connection[Any],
        meeting_id: str,
        transcript_id: str,
        asr_job_id: str,
        storage_key: str | None,
        language: str,
        acoustic_profile: str,
        correlation_id: str | None,
        duration_ms: int | None = None,
    ) -> str | None:
        """Repair the V1→enrichment handoff without creating a second job.

        The meeting row is already locked by the caller. The job lookup is
        therefore serialized with a concurrent retry, and the outbox check
        closes the crash window between job creation and event publication.
        FAILED/CANCELLED jobs are left for the explicit retry endpoint.
        """
        enrichment = connection.execute(
            "SELECT id,status FROM jobs WHERE input_transcript_id=%s AND type='TRANSCRIPT_ENRICH' ORDER BY CASE WHEN status IN ('QUEUED','RUNNING') THEN 0 WHEN status IN ('READY','ENRICHED_READY') THEN 1 ELSE 2 END, created_at LIMIT 1 FOR UPDATE",
            (transcript_id,),
        ).fetchone()
        if enrichment is None:
            enrichment = connection.execute(
                "INSERT INTO jobs(id,meeting_id,type,status,stage,progress,input_transcript_id,pipeline_correlation_id) VALUES(gen_random_uuid(),%s,'TRANSCRIPT_ENRICH','QUEUED','ASR_READY',0,%s,%s) ON CONFLICT DO NOTHING RETURNING id,status",
                (meeting_id, transcript_id, correlation_id),
            ).fetchone()
            if enrichment is None:
                enrichment = connection.execute(
                    "SELECT id,status FROM jobs WHERE input_transcript_id=%s AND type='TRANSCRIPT_ENRICH' ORDER BY CASE WHEN status IN ('QUEUED','RUNNING') THEN 0 WHEN status IN ('READY','ENRICHED_READY') THEN 1 ELSE 2 END, created_at LIMIT 1 FOR UPDATE",
                    (transcript_id,),
                ).fetchone()
        enrichment_id, status = str(enrichment[0]), str(enrichment[1])
        connection.execute(
            "UPDATE recording_pipeline_runs SET enrichment_job_id=%s,updated_at=now() WHERE asr_job_id=%s",
            (enrichment_id, asr_job_id),
        )
        if status in {"READY", "ENRICHED_READY", "FAILED", "CANCELLED"}:
            return enrichment_id
        exists = connection.execute(
            "SELECT EXISTS(SELECT 1 FROM outbox_messages WHERE topic='ml.transcribe' AND payload->>'job_id'=%s)",
            (enrichment_id,),
        ).fetchone()[0]
        if not exists:
            message_id = connection.execute("SELECT gen_random_uuid()").fetchone()[0]
            payload = json.dumps({
                "message_id": str(message_id),
                "job_id": enrichment_id,
                "meeting_id": meeting_id,
                "transcript_id": transcript_id,
                "stage": "ASR_READY",
                "storage_key": storage_key,
                "language": language or "ru",
                "acousticProfile": acoustic_profile or "AUTO",
                "correlation_id": correlation_id,
                "duration_ms": duration_ms,
            })
            connection.execute("INSERT INTO outbox_messages(id,topic,payload) VALUES(%s,'ml.transcribe',%s::jsonb)", (message_id, payload))
        return enrichment_id

    def persist_result(self, job_id: str, meeting_id: str, result: dict[str, Any]) -> bool:
        with self._db.connection() as connection:
            # Serialize transcript versions and summary-job creation per meeting.
            meeting = connection.execute("SELECT status FROM meetings WHERE id=%s FOR UPDATE", (meeting_id,)).fetchone()
            if meeting is None or str(meeting[0]) == "CANCELLED":
                return False
            job = connection.execute("SELECT status,type,pipeline_correlation_id,input_transcript_id FROM jobs WHERE id=%s FOR UPDATE", (job_id,)).fetchone()
            if job is None or str(job[0]) == "CANCELLED":
                return False
            # Enrichment is a retried, idempotent transformation of V1.  If a
            # process died after committing V2 but before acknowledging NATS,
            # return the existing version and never append a second one.
            if str(job[1]) == ENRICHMENT_JOB_TYPE:
                existing_enriched = connection.execute(
                    "SELECT id FROM transcripts WHERE meeting_id=%s AND version_kind='ENRICHED' AND (quality_metadata->>'processing_job_id'=%s OR source_transcript_id=%s) ORDER BY version DESC LIMIT 1",
                    (meeting_id, job_id, job[3]),
                ).fetchone()
                if existing_enriched:
                    connection.execute("UPDATE recording_pipeline_runs SET transcript_v2_id=%s,updated_at=now() WHERE enrichment_job_id=%s", (existing_enriched[0], job_id))
                    connection.execute("UPDATE jobs SET status='READY',stage='ENRICHED_READY',progress=100,lease_expires_at=NULL,last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'", (job_id,))
                    return True
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
            warnings = list(result.get("warnings") or [])
            quality = result.get("quality") or {}
            no_speech_detected = result_error_code == "NO_SPEECH_DETECTED" or "NO_SPEECH_DETECTED" in {str(item).upper() for item in warnings}
            if no_speech_detected and result_error_code is None:
                result_error_code = "NO_SPEECH_DETECTED"
            quality_codes = {"ASR_LANGUAGE_MISMATCH", "AUDIO_SIGNAL_WEAK", "AUDIO_SIGNAL_UNUSABLE", "ASR_ENHANCEMENT_FAILED"}
            quality_values = {str(item).upper() for item in warnings}
            quality_values.update(str(item).upper() for item in (quality.get("reasons") or []))
            quality_values.add(str(result_error_code or "").upper())
            needs_review = str(transcript_status).upper() in {"PARTIAL_READY", "NEEDS_REVIEW", "REQUIRES_REVIEW"}
            needs_review = needs_review or "NEEDS_REVIEW" in quality_values or "REQUIRES_REVIEW" in quality_values
            summary_blocked = version_kind != "ENRICHED" or no_speech_detected or needs_review or bool(quality_values & quality_codes)
            if summary_blocked and version_kind == "ENRICHED" and not no_speech_detected and "SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY" not in warnings:
                warnings.append("SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY")
                if transcript_status == "READY":
                    transcript_status = "PARTIAL_READY"
            transcript_id = connection.execute(
                "INSERT INTO transcripts(id,meeting_id,version,status,language,model_name,warnings,quality_metadata,quality_score,processing_profile,selected_asr_pass,source_transcript_id,version_kind) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s::jsonb,%s::jsonb,%s,%s,%s,%s,%s) RETURNING id",
                (meeting_id, version, transcript_status, result.get("language"), result.get("metadata", {}).get("model"), json.dumps(warnings), json.dumps(quality), quality.get("quality_score"), result.get("metadata", {}).get("processing_profile"), quality.get("selected_pass"), source_transcript_id, version_kind),
            ).fetchone()[0]
            if version_kind == "ENRICHED":
                connection.execute("UPDATE recording_pipeline_runs SET transcript_v2_id=%s,updated_at=now() WHERE enrichment_job_id=%s", (transcript_id, job_id))
            segments = [segment for segment in result.get("segments", []) if isinstance(segment, dict)]
            speakers: dict[str, str] = {}
            speaker_registry: dict[str, Any] = {"status": "UNMATCHED", "reason": "DIARIZATION_EMBEDDINGS_UNAVAILABLE", "speakers": {}}
            profiles: list[dict[str, Any]] = []
            owner_row = connection.execute("SELECT owner_id FROM meetings WHERE id=%s", (meeting_id,)).fetchone()
            if owner_row and owner_row[0]:
                profile_rows = connection.execute(
                    "SELECT id,embedding_centroid,display_name FROM speaker_profiles WHERE owner_user_id=%s AND status='ACTIVE'",
                    (owner_row[0],),
                ).fetchall()
                for profile_id, centroid, display_name in profile_rows:
                    try:
                        centroid_value = centroid if isinstance(centroid, (list, dict)) else json.loads(centroid or "[]")
                    except (TypeError, ValueError, json.JSONDecodeError):
                        centroid_value = []
                    profiles.append({"id": str(profile_id), "embedding_centroid": centroid_value, "display_name": display_name})
            for segment in segments:
                label = normalize_speaker_label(segment.get("speaker"))
                if not label or label in speakers:
                    continue
                embedding = _speaker_embedding(result, label, segments)
                match = match_profile(embedding, profiles)
                display_name = default_display_label(label)
                if match.status == "MATCHED":
                    profile = next((item for item in profiles if item["id"] == match.profile_id), None)
                    display_name = str(profile["display_name"]) if profile else display_name
                    speaker_registry["status"] = "MATCHED"
                elif match.status == "SUGGESTION":
                    speaker_registry["status"] = "SUGGESTION"
                candidate_profile = next((item for item in profiles if item["id"] == (match.profile_id or match.suggestion_profile_id)), None)
                suggestion_name = candidate_profile.get("display_name") if candidate_profile and match.status == "SUGGESTION" else None
                speaker_registry["speakers"][label] = {
                    "status": match.status,
                    "confidence": match.confidence,
                    "profile_id": match.profile_id or match.suggestion_profile_id,
                    "display_name": candidate_profile.get("display_name") if candidate_profile else None,
                    "reason": match.reason,
                }
                if match.status == "MATCHED" and match.profile_id:
                    # Stats are updated only after a confident match.  The
                    # existence check keeps retries/reprocessing idempotent.
                    connection.execute(
                        "UPDATE speaker_profiles SET meetings_count=meetings_count + CASE WHEN EXISTS (SELECT 1 FROM meeting_speakers WHERE meeting_id=%s AND speaker_profile_id=%s) THEN 0 ELSE 1 END, confidence=GREATEST(COALESCE(confidence,0),%s), last_seen_at=now(), updated_at=now() WHERE id=%s",
                        (meeting_id, match.profile_id, match.confidence, match.profile_id),
                    )
                speaker_id = connection.execute(
                    "INSERT INTO meeting_speakers(id,meeting_id,stable_key,display_name,confidence,speaker_profile_id,profile_confidence,profile_match_status,profile_match_reason,profile_suggestion_name) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s,%s,%s,%s) ON CONFLICT(meeting_id,stable_key) DO UPDATE SET display_name=excluded.display_name,confidence=excluded.confidence,speaker_profile_id=excluded.speaker_profile_id,profile_confidence=excluded.profile_confidence,profile_match_status=excluded.profile_match_status,profile_match_reason=excluded.profile_match_reason,profile_suggestion_name=excluded.profile_suggestion_name RETURNING id",
                    (meeting_id, label, display_name, match.confidence, match.profile_id, match.confidence, match.status, match.reason, suggestion_name),
                ).fetchone()[0]
                speakers[label] = str(speaker_id)
            quality["speaker_registry"] = speaker_registry
            connection.execute("UPDATE transcripts SET quality_metadata=%s::jsonb WHERE id=%s", (json.dumps(quality), transcript_id))
            technical_intervals = _technical_intervals(connection, meeting_id)
            for ordinal, segment in enumerate(segments):
                label = normalize_speaker_label(segment.get("speaker"))
                start_ms = int(float(segment.get("start", 0)) * 1000)
                end_ms = int(float(segment.get("end", 0)) * 1000)
                technical_kind, technical_hidden = segment_technical_flags(start_ms, end_ms, technical_intervals)
                connection.execute(
                    "INSERT INTO transcript_segments(id,transcript_id,ordinal,start_ms,end_ms,speaker_id,speaker_label,text,confidence,words,segment_kind,is_hidden) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s) ON CONFLICT(transcript_id,ordinal) DO UPDATE SET text=excluded.text,end_ms=excluded.end_ms,speaker_id=excluded.speaker_id,speaker_label=excluded.speaker_label,confidence=excluded.confidence,words=excluded.words,segment_kind=excluded.segment_kind,is_hidden=excluded.is_hidden",
                    (transcript_id, ordinal, start_ms, end_ms, speakers.get(label) if label else None, label or "UNKNOWN", str(segment.get("text", "")).strip(), segment.get("confidence"), Jsonb(segment.get("words", [])), technical_kind if technical_hidden else str(segment.get("segment_kind", "SPEECH")), bool(segment.get("is_hidden", False)) or technical_hidden),
                )
            if os.getenv("AUTO_SUMMARY_ENABLED", "false").lower() in {"1", "true", "yes"} and not summary_blocked:
                summary_profile = os.getenv("AUTO_SUMMARY_PROFILE", "MEETING_PROTOCOL_RU").strip().upper()
                prompt_version = os.getenv("AUTO_SUMMARY_PROMPT_VERSION", "meeting-protocol-ru-v2")
                summary_job_id = self._ensure_summary_job_and_outbox(
                    connection,
                    meeting_id,
                    str(transcript_id),
                    str(result.get("source_hash") or ""),
                    summary_profile,
                    prompt_version,
                    str(result.get("correlation_id") or correlation_id) if (result.get("correlation_id") or correlation_id) else None,
                )
                if summary_job_id is None:
                    connection.execute("UPDATE meetings SET status='PARTIAL_READY' WHERE id=%s", (meeting_id,))
                else:
                    summary_state = connection.execute("SELECT status FROM jobs WHERE id=%s", (summary_job_id,)).fetchone()
                    connection.execute(
                        "UPDATE meetings SET status=%s WHERE id=%s",
                        ("READY" if summary_state and str(summary_state[0]) == "READY" else "SUMMARIZING", meeting_id),
                    )
            else:
                connection.execute(
                    "UPDATE meetings SET status=%s WHERE id=%s",
                    # Compatibility contract: "PARTIAL_READY" if no_speech_detected else "TRANSCRIPT_READY"
                    ("PARTIAL_READY" if no_speech_detected or (summary_blocked and version_kind == "ENRICHED") else "TRANSCRIPT_READY", meeting_id),
                )
            result_error_message = "Речь не обнаружена в корректном аудиофайле." if result_error_code == "NO_SPEECH_DETECTED" else None
            final_stage = "ENRICHED_READY" if version_kind == "ENRICHED" else "ASR_READY"
            connection.execute("UPDATE jobs SET status='READY',stage=%s,progress=100,error_message=%s,error_code=%s,lease_expires_at=NULL,last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'", (final_stage, result_error_message, result_error_code, job_id))
            return True

    @staticmethod
    def _ensure_summary_job_and_outbox(
        connection: psycopg.Connection[Any],
        meeting_id: str,
        transcript_id: str,
        source_hash: str,
        summary_profile: str,
        prompt_version: str,
        correlation_id: str | None,
    ) -> str | None:
        """Reconcile the V2→Summary handoff after a worker restart.

        The meeting lock held by ``persist_result`` serializes this lookup.
        Existing queued/running jobs are reused and their outbox event is
        repaired if the process died between the two commits. Terminal jobs
        are not silently duplicated; the explicit rebuild endpoint owns that
        transition.
        """
        summary_job = connection.execute(
            "SELECT id,status FROM jobs WHERE input_transcript_id=%s AND type='SUMMARIZE' ORDER BY CASE WHEN status IN ('QUEUED','RUNNING') THEN 0 WHEN status='READY' THEN 1 ELSE 2 END, created_at LIMIT 1 FOR UPDATE",
            (transcript_id,),
        ).fetchone()
        if summary_job is None:
            summary_job = connection.execute(
                "INSERT INTO jobs(id,meeting_id,type,status,stage,progress,input_transcript_id,pipeline_correlation_id) VALUES(gen_random_uuid(),%s,'SUMMARIZE','QUEUED','TRANSCRIPT_READY',0,%s,%s) ON CONFLICT DO NOTHING RETURNING id,status",
                (meeting_id, transcript_id, correlation_id),
            ).fetchone()
            if summary_job is None:
                summary_job = connection.execute(
                    "SELECT id,status FROM jobs WHERE input_transcript_id=%s AND type='SUMMARIZE' ORDER BY CASE WHEN status IN ('QUEUED','RUNNING') THEN 0 WHEN status='READY' THEN 1 ELSE 2 END, created_at LIMIT 1 FOR UPDATE",
                    (transcript_id,),
                ).fetchone()
        summary_job_id, status = str(summary_job[0]), str(summary_job[1])
        connection.execute(
            "UPDATE recording_pipeline_runs SET summary_job_id=%s,updated_at=now() WHERE transcript_v2_id=%s",
            (summary_job_id, transcript_id),
        )
        if status in {"READY", "FAILED", "CANCELLED"}:
            return None if status in {"FAILED", "CANCELLED"} else summary_job_id
        exists = connection.execute(
            "SELECT EXISTS(SELECT 1 FROM outbox_messages WHERE topic='llm.summarize' AND payload->>'job_id'=%s)",
            (summary_job_id,),
        ).fetchone()[0]
        if not exists:
            message_id = connection.execute("SELECT gen_random_uuid()").fetchone()[0]
            payload = json.dumps({
                "message_id": str(message_id),
                "job_id": summary_job_id,
                "meeting_id": meeting_id,
                "transcript_id": transcript_id,
                "source_hash": source_hash,
                "summary_profile": summary_profile,
                "prompt_version": prompt_version,
                "meeting_context": {},
                "correlation_id": correlation_id,
            })
            connection.execute("INSERT INTO outbox_messages(id,topic,payload) VALUES(%s,'llm.summarize',%s::jsonb)", (message_id, payload))
        return summary_job_id
