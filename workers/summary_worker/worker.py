from __future__ import annotations

import asyncio
import hashlib
import json
import os
import socket
import threading
import time
from datetime import datetime
from pathlib import Path
from typing import Any
import logging

from psycopg.types.json import Jsonb

from workers.gpu_lease import PostgresGpuLease
from workers.gpu_runtime_coordination import GpuRuntimeCoordinator
from workers.db_pool import DatabaseConnectionPool
from workers.nats_utils import ensure_stream, fetch_available, maintain_message
from workers.runtime_heartbeat import AsyncHeartbeat
from whisperx_atom.domain import require_meeting_id
from whisperx_atom.pipeline_contract import validate_stage_name
from .contracts import (
    MEETING_PROTOCOL_RU,
    PROTOCOL_RU_PROMPT_VERSION,
    PROTOCOL_RU_SCHEMA_VERSION,
    MeetingContext,
    SUMMARY_PROMPT_VERSION,
    SUMMARY_SCHEMA_VERSION,
)
from .summarizer import LlamaCppClient, SummaryOrchestrator, TranscriptSegment
from .llama_subprocess import LocalLlamaRuntime
from .assistant import AssistantGpuWaitScheduled, AssistantMessageAlreadyClaimed, AssistantRetryScheduled, AssistantWorker

LOGGER = logging.getLogger("whisperx.summary-worker")

# Summary and Assistant share one process-wide llama runtime.  Coordination
# ownership is for that runtime, not for the NATS consumer that requested it.
# Keeping one owner prevents a normal summary -> assistant handoff from being
# misclassified as an ownership conflict.
LLM_RUNTIME_OWNER = "llm-runtime"


class RetryScheduled(RuntimeError):
    def __init__(self, delay_seconds: float):
        super().__init__("SUMMARY_RETRY_SCHEDULED")
        self.delay_seconds = delay_seconds


def is_retryable_summary_error(exc: BaseException) -> bool:
    text = f"{type(exc).__name__}: {exc}".lower()
    if any(token in text for token in (
        "transcript_has_no_segments",
        "transcript_not_found",
        "cancelled",
        "summary_schema_invalid",
        "schema_validation",
        "invalid_json",
        "evidence_invalid",
    )):
        return False
    # Only infrastructure failures are retried. Deterministic prompt/schema
    # failures must become FAILED instead of consuming the whole retry budget.
    return any(token in text for token in (
        "timeout", "timed out", "connection", "unavailable", "refused",
        "llama", "gpu", "cuda", "busy", "temporarily", "503", "502",
        "sqlite", "postgres", "psycopg", "broken pipe",
    ))


def retry_delay_seconds(attempt: int) -> float:
    return {1: 5, 2: 15, 3: 30, 4: 60, 5: 120}.get(attempt, 300) + attempt * 0.37


class SummaryRepository:
    def __init__(self) -> None:
        self.conninfo = os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")
        self._db = DatabaseConnectionPool(self.conninfo, "summary-worker")

    def close(self) -> None:
        self._db.close()

    def reset_stale_leases(self) -> int:
        """Requeue summary jobs abandoned by a worker restart.

        Summary generation is optional and can legitimately run for several
        minutes, so an inbox lease cannot be treated as proof that a worker is
        still alive.  The worker heartbeat is the ownership signal.  Only
        SUMMARIZE jobs are touched here; assistant and transcript jobs have
        their own recovery loops.
        """
        stale_seconds = max(30, int(os.getenv("SUMMARY_STALE_LEASE_SECONDS", "90")))
        with self._db.connection() as connection:
            with connection.transaction():
                connection.execute(
                    f"""
                    UPDATE inbox_messages AS inbox
                    SET lease_expires_at=now() - interval '1 second', worker_id=NULL
                    FROM jobs AS job
                    WHERE inbox.job_id=job.id
                      AND job.type='SUMMARIZE'
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
                        stage='TRANSCRIPT_READY',
                        worker_id=NULL,
                        lease_expires_at=NULL,
                        last_heartbeat=NULL,
                        error_code='WORKER_RESTART_RECOVERY',
                        updated_at=now()
                    WHERE type='SUMMARIZE'
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

    def claim(self, message_id: str, job_id: str) -> bool:
        with self._db.connection() as connection:
            row = connection.execute(
                """
                INSERT INTO inbox_messages(message_id, job_id, lease_expires_at, worker_id)
                VALUES(%s,%s,now()+interval '30 minutes',%s)
                ON CONFLICT(message_id) DO UPDATE SET lease_expires_at=excluded.lease_expires_at,worker_id=excluded.worker_id
                WHERE inbox_messages.lease_expires_at IS NULL OR inbox_messages.lease_expires_at < now()
                RETURNING message_id
                """,
                (message_id, job_id, socket.gethostname()),
            ).fetchone()
            return row is not None

    def job_state(self, job_id: str) -> str | None:
        with self._db.connection() as connection:
            row = connection.execute("SELECT status FROM jobs WHERE id=%s", (job_id,)).fetchone()
            return str(row[0]) if row else None

    def release_message(self, message_id: str | None) -> None:
        if not message_id:
            return
        with self._db.connection() as connection:
            connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (message_id,))

    def pipeline_correlation(self, job_id: str, meeting_id: str) -> str | None:
        with self._db.connection() as connection:
            row = connection.execute("SELECT pipeline_correlation_id FROM jobs WHERE id=%s", (job_id,)).fetchone()
            if row and row[0]:
                return str(row[0])
            row = connection.execute(
                "SELECT pipeline_correlation_id FROM recording_sessions WHERE meeting_id=%s ORDER BY created_at DESC LIMIT 1",
                (meeting_id,),
            ).fetchone()
            return str(row[0]) if row and row[0] else None

    def record_pipeline_metrics(self, job_id: str, meeting_id: str, metrics: dict[str, Any] | None) -> None:
        """Persist summary timings alongside the recording lineage.

        Only bounded numeric diagnostics are stored.  This keeps the
        per-recording trace useful after logs rotate without copying prompt,
        answer or transcript content into ``stage_timings``.
        """
        if not isinstance(metrics, dict):
            return
        safe: dict[str, float | int] = {}
        for key, value in metrics.items():
            if isinstance(key, str) and len(key) <= 80 and isinstance(value, (int, float)) and not isinstance(value, bool):
                numeric = float(value)
                if numeric >= 0 and numeric == numeric and numeric != float("inf"):
                    safe[key] = int(numeric) if isinstance(value, int) else round(numeric, 3)
        if not safe:
            return
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
            if session:
                connection.execute(
                    "UPDATE recording_sessions SET stage_timings=COALESCE(stage_timings,'{}'::jsonb) || %s::jsonb WHERE id=%s",
                    (Jsonb(safe), session[0]),
                )

    def update_job(self, job_id: str, status: str, stage: str, progress: int, error: str | None = None) -> None:
        stage = validate_stage_name(stage)
        with self._db.connection() as connection:
            connection.execute(
                "UPDATE jobs SET status=%s,stage=%s,progress=%s,error_message=%s,worker_id=%s,lease_expires_at=now()+interval '30 minutes',last_heartbeat=now(),updated_at=now() WHERE id=%s AND status <> 'CANCELLED'",
                (status, stage, progress, error, socket.gethostname(), job_id),
            )

    def renew_lease(self, job_id: str, message_id: str | None = None) -> None:
        with self._db.connection() as connection:
            connection.execute(
                "UPDATE jobs SET lease_expires_at=now()+interval '30 minutes',last_heartbeat=now() WHERE id=%s AND status NOT IN ('READY','FAILED','CANCELLED')",
                (job_id,),
            )
            if message_id:
                connection.execute(
                    "UPDATE inbox_messages SET lease_expires_at=now()+interval '30 minutes',worker_id=%s WHERE message_id=%s",
                    (socket.gethostname(), message_id),
                )

    def mark_failed(self, job_id: str, meeting_id: str, error: str, message_id: str | None = None) -> None:
        with self._db.connection() as connection:
            with connection.transaction():
                connection.execute(
                    "UPDATE jobs SET status='FAILED',stage='FAILED',progress=0,error_message=%s,error_code='SUMMARY_FAILED',lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now() WHERE id=%s AND status <> 'CANCELLED'",
                    (error, job_id),
                )
                # The transcript remains usable even when the optional summary failed.
                connection.execute("UPDATE meetings SET status='PARTIAL_READY' WHERE id=%s AND status <> 'CANCELLED'", (meeting_id,))
                # Terminal jobs must not leave a live inbox lease behind. This
                # avoids one pointless NAK/redelivery and bounds inbox growth.
                if message_id:
                    connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (message_id,))

    def schedule_retry(self, job_id: str, error: str, error_code: str, message_id: str | None = None, max_attempts: int = 3) -> int | None:
        """Atomically make a transient job retryable and release its inbox claim.

        Releasing the inbox row in the same transaction as the job transition is
        essential: a delayed NAK can arrive immediately after the transaction,
        and a still-live inbox lease would make the retry look like a duplicate
        and be acknowledged without executing the job.
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
    def load_segments(self, meeting_id: str, transcript_id: str | None = None) -> list[TranscriptSegment]:
        with self._db.connection() as connection:
            if transcript_id:
                rows = connection.execute(
                    """
                    SELECT s.id,s.start_ms,s.end_ms,COALESCE(ms.display_name,s.speaker_label,'Спикер N'),s.text
                    FROM transcript_segments s
                    JOIN transcripts t ON t.id=s.transcript_id
                    LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id
                    WHERE t.id=%s AND t.meeting_id=%s AND COALESCE(s.is_hidden,false)=false
                    ORDER BY s.ordinal
                    """,
                    (transcript_id, meeting_id),
                ).fetchall()
            else:
                rows = connection.execute(
                    """
                    SELECT s.id,s.start_ms,s.end_ms,COALESCE(ms.display_name,s.speaker_label,'Спикер N'),s.text
                    FROM transcript_segments s
                    JOIN transcripts t ON t.id=s.transcript_id
                    LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id
                    WHERE t.meeting_id=%s AND t.version=(SELECT MAX(version) FROM transcripts WHERE meeting_id=%s) AND COALESCE(s.is_hidden,false)=false
                    ORDER BY s.ordinal
                    """,
                    (meeting_id, meeting_id),
                ).fetchall()
            marker_rows = connection.execute(
                """
                SELECT e.event_type,e.media_time_ms,e.payload
                FROM recording_events e
                JOIN recording_sessions rs ON rs.id=e.session_id
                WHERE rs.meeting_id=%s
                  AND e.media_time_ms IS NOT NULL
                  AND e.event_type IN ('MARKER','DECISION','ACTION_ITEM','VOICE_COMMAND')
                ORDER BY e.media_time_ms
                """,
                (meeting_id,),
            ).fetchall()
        markers: list[tuple[str, int]] = []
        for event_type, media_time_ms, payload in marker_rows:
            label = str(event_type).upper()
            if isinstance(payload, dict):
                detail = payload.get("label") or payload.get("text") or payload.get("command")
                if detail:
                    label += ":" + str(detail).strip()[:120]
            markers.append((label, int(media_time_ms)))
        return [
            TranscriptSegment(
                str(row[0]),
                int(row[1]),
                int(row[2]),
                str(row[3]),
                str(row[4]),
                tuple(label for label, at_ms in markers if int(row[2]) >= at_ms - 60_000 and int(row[1]) <= at_ms + 120_000),
            )
            for row in rows
        ]

    def persist(self, job_id: str, meeting_id: str, transcript_id: str | None, result: dict[str, Any], model_name: str) -> bool:
        source_hash = str(result["source_hash"])
        is_protocol = str(result.get("profile", "")).upper() == MEETING_PROTOCOL_RU
        schema_version = str(result.get("schema_version") or (PROTOCOL_RU_SCHEMA_VERSION if is_protocol else SUMMARY_SCHEMA_VERSION))
        prompt_version = str(result.get("prompt_version") or (PROTOCOL_RU_PROMPT_VERSION if is_protocol else SUMMARY_PROMPT_VERSION))
        # Keep a stable provenance marker in JSONB without requiring a schema
        # migration. A changed transcript, model, prompt or schema therefore
        # produces a new logical generation even when the NATS job is retried.
        result["generation_fingerprint"] = ":".join((source_hash, model_name, prompt_version, schema_version))
        with self._db.connection() as connection:
            # Serialize summary versions and decision/task inserts for this meeting.
            meeting = connection.execute("SELECT status FROM meetings WHERE id=%s FOR UPDATE", (meeting_id,)).fetchone()
            if meeting is None or str(meeting[0]) == "CANCELLED":
                return False
            job = connection.execute("SELECT status,pipeline_correlation_id FROM jobs WHERE id=%s FOR UPDATE", (job_id,)).fetchone()
            if job is None or str(job[0]) == "CANCELLED":
                return False
            correlation_id = result.get("correlation_id") or (str(job[1]) if job[1] else None)
            if not correlation_id:
                fallback = connection.execute(
                    "SELECT pipeline_correlation_id FROM recording_sessions WHERE meeting_id=%s ORDER BY created_at DESC LIMIT 1",
                    (meeting_id,),
                ).fetchone()
                correlation_id = str(fallback[0]) if fallback and fallback[0] else None
            if correlation_id:
                result["correlation_id"] = correlation_id
                connection.execute("UPDATE jobs SET pipeline_correlation_id=%s WHERE id=%s", (correlation_id, job_id))
            if transcript_id:
                transcript = connection.execute("SELECT id FROM transcripts WHERE id=%s AND meeting_id=%s", (transcript_id, meeting_id)).fetchone()
            else:
                transcript = connection.execute("SELECT id FROM transcripts WHERE meeting_id=%s ORDER BY version DESC LIMIT 1", (meeting_id,)).fetchone()
            if transcript is None:
                raise RuntimeError("transcript_not_found")
            existing_summary = connection.execute("SELECT id,status FROM summaries WHERE job_id=%s FOR UPDATE", (job_id,)).fetchone()
            summary_id = None
            if existing_summary is not None:
                existing_status = str(existing_summary[1])
                if existing_status in {"READY", "NEEDS_REVIEW"}:
                    connection.execute("UPDATE jobs SET status='READY',stage='READY',progress=100,error_message=NULL,error_code=NULL,lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now() WHERE id=%s AND status <> 'CANCELLED'", (job_id,))
                    connection.execute("UPDATE recording_pipeline_runs SET summary_id=%s,summary_job_id=%s,updated_at=now() WHERE summary_job_id=%s", (existing_summary[0], job_id, job_id))
                    connection.execute("UPDATE meetings SET status=%s WHERE id=%s AND status <> 'CANCELLED'", ("READY" if existing_status == "READY" else "PARTIAL_READY", meeting_id))
                    return True
                if existing_status != "FAILED":
                    raise RuntimeError("summary_persist_incomplete")
                # A manual retry reuses the idempotency row but resets its
                # derived children. This preserves the unique job_id index
                # while ensuring a FAILED result can never be promoted to
                # READY without new generated content.
                summary_id = existing_summary[0]
                connection.execute("DELETE FROM summary_evidence WHERE summary_id=%s", (summary_id,))
                connection.execute("DELETE FROM decisions WHERE summary_id=%s", (summary_id,))
                connection.execute("DELETE FROM action_items WHERE summary_id=%s", (summary_id,))
                current = connection.execute("SELECT COALESCE(MAX(version),0) FROM summaries WHERE meeting_id=%s AND id<>%s", (meeting_id, summary_id)).fetchone()[0]
                connection.execute(
                    "UPDATE summaries SET transcript_id=%s,version=%s,status='DRAFT',model_name=%s,prompt_version=%s,schema_version=%s,source_hash=%s,quality_score=%s,content=%s::jsonb WHERE id=%s",
                    (transcript[0], int(current) + 1, model_name, prompt_version, schema_version, source_hash, result.get("quality_score"), Jsonb(result), summary_id),
                )
            else:
                current = connection.execute("SELECT COALESCE(MAX(version),0) FROM summaries WHERE meeting_id=%s", (meeting_id,)).fetchone()[0]
                summary_id = connection.execute(
                    "INSERT INTO summaries(id,job_id,meeting_id,transcript_id,version,status,model_name,prompt_version,schema_version,source_hash,quality_score,content) VALUES(gen_random_uuid(),%s,%s,%s,%s,'DRAFT',%s,%s,%s,%s,%s,%s::jsonb) RETURNING id",
                    (
                        job_id,
                        meeting_id,
                        transcript[0],
                        int(current) + 1,
                        model_name,
                        prompt_version,
                        schema_version,
                        source_hash,
                        result.get("quality_score"),
                        Jsonb(result),
                    ),
                ).fetchone()[0]
            connection.execute(
                "INSERT INTO summary_runs(id,summary_id,model_name,prompt_version,schema_version,source_hash,finished_at,block_count,input_tokens,output_tokens,generation_ms,quality_score) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,now(),%s,%s,%s,%s,%s)",
                (
                    summary_id,
                    model_name,
                    prompt_version,
                    schema_version,
                    source_hash,
                    result.get("block_count", 0),
                    result.get("input_tokens"),
                    result.get("output_tokens"),
                    result.get("generation_ms"),
                    result.get("quality_score"),
                ),
            )
            valid_segments = {str(row[0]): (int(row[1]), int(row[2])) for row in connection.execute("SELECT id,start_ms,end_ms FROM transcript_segments WHERE transcript_id=%s", (transcript[0],)).fetchall()}
            collections = ("questions_and_decisions", "tasks") if is_protocol else ("decisions", "risks", "open_questions", "topics", "notable_facts")
            for collection in collections:
                for index, item in enumerate(result.get(collection, [])):
                    evidence = [str(value) for value in item.get("evidence_segment_ids", []) if str(value) in valid_segments]
                    for segment_id in evidence:
                        start_ms, end_ms = valid_segments[segment_id]
                        connection.execute("INSERT INTO summary_evidence(id,summary_id,entity_type,entity_key,segment_id,start_ms,end_ms) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s)", (summary_id, collection, str(index), segment_id, start_ms, end_ms))
                    decision_text = item.get("decision", item.get("text"))
                    if collection in {"decisions", "questions_and_decisions"} and decision_text:
                        connection.execute("INSERT INTO decisions(id,meeting_id,summary_id,text,status) VALUES(gen_random_uuid(),%s,%s,%s,'DRAFT')", (meeting_id, summary_id, str(decision_text)))
            for index, item in enumerate(result.get("action_items", [])):
                task_text = str(item.get("task", "")).strip()
                if not task_text:
                    continue
                evidence = [str(value) for value in item.get("evidence_segment_ids", []) if str(value) in valid_segments]
                evidence_id = evidence[0] if evidence else None
                connection.execute(
                    "INSERT INTO action_items(id,meeting_id,summary_id,task,responsible,deadline,status,evidence_segment_id) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s,%s)",
                    (meeting_id, summary_id, task_text, item.get("responsible"), parse_deadline(item.get("deadline_iso", item.get("deadline"))), "NEEDS_REVIEW", evidence_id),
                )
            if is_protocol:
                for index, item in enumerate(result.get("tasks", [])):
                    task_text = str(item.get("task", "")).strip()
                    if not task_text:
                        continue
                    evidence = [str(value) for value in item.get("evidence_segment_ids", []) if str(value) in valid_segments]
                    evidence_id = evidence[0] if evidence else None
                    connection.execute(
                        "INSERT INTO action_items(id,meeting_id,summary_id,task,responsible,deadline,status,evidence_segment_id) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s,%s)",
                        (meeting_id, summary_id, task_text, None, parse_deadline(item.get("deadline_iso")), "NEEDS_REVIEW", evidence_id),
                    )
            validation = result.get("validation") if isinstance(result.get("validation"), dict) else {}
            quality = result.get("quality") if is_protocol and isinstance(result.get("quality"), dict) else {}
            quality_status = str(quality.get("status", "READY"))
            needs_review = (
                quality_status != "READY"
                if is_protocol
                else bool(
                    validation.get("rejected_facts")
                    or validation.get("needs_review_items", validation.get("review_items"))
                    or validation.get("review_reasons")
                )
            )
            summary_status = (
                quality_status if quality_status in {"READY", "NEEDS_REVIEW", "FAILED"}
                else ("NEEDS_REVIEW" if needs_review else "READY")
            )
            connection.execute("UPDATE summaries SET status=%s WHERE id=%s", (summary_status, summary_id))
            connection.execute("UPDATE recording_pipeline_runs SET summary_id=%s,summary_job_id=%s,updated_at=now() WHERE summary_job_id=%s", (summary_id, job_id, job_id))
            if summary_status == "FAILED":
                connection.execute("UPDATE jobs SET status='FAILED',stage='FAILED',progress=100,error_message=%s,error_code='SUMMARY_PROTOCOL_QUALITY_FAILED',lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now() WHERE id=%s AND status <> 'CANCELLED'", ("Не удалось сформировать подтверждённый протокол", job_id))
            else:
                connection.execute("UPDATE jobs SET status='READY',stage='READY',progress=100,error_message=NULL,error_code=NULL,lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now() WHERE id=%s AND status <> 'CANCELLED'", (job_id,))
            connection.execute("UPDATE meetings SET status=%s WHERE id=%s AND status <> 'CANCELLED'", ("READY" if summary_status == "READY" else "PARTIAL_READY", meeting_id))
            return True

def parse_deadline(value: Any) -> datetime | None:
    if not value or not isinstance(value, str):
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


class SummaryWorker:
    def __init__(self, gpu_coordination: GpuRuntimeCoordinator | None = None,
                 llm_runtime: LocalLlamaRuntime | None = None,
                 llm_owner: str | None = None) -> None:
        self.repository = SummaryRepository()
        self._gpu_lease = PostgresGpuLease(self.repository.conninfo, priority=100)
        self._gpu_coordination = gpu_coordination or GpuRuntimeCoordinator(self.repository.conninfo)
        # Standalone construction (tests/rolling launchers) still gets a
        # process-scoped owner; production injects the same owner into both
        # SummaryWorker and AssistantWorker from run().
        self._llm_owner = llm_owner or self._gpu_coordination.owner
        self.model_alias = os.getenv("LLM_MODEL_ALIAS", "qwen3-8b")
        self._llm_runtime = llm_runtime or LocalLlamaRuntime()
        self._llm_client: LlamaCppClient | None = None

    def _client_for(self, base_url: str) -> LlamaCppClient:
        normalized = base_url.rstrip("/")
        if self._llm_client is None or not self._llm_client.url.startswith(normalized):
            self._llm_client = LlamaCppClient(normalized, self.model_alias)
        return self._llm_client

    async def close(self) -> None:
        if self._llm_client is not None:
            await self._llm_client.aclose()
            self._llm_client = None
        self.repository.close()


    async def handle(self, payload: dict[str, Any]) -> None:
        started_at = time.perf_counter()
        job_id = str(payload["job_id"])
        meeting_id = str(require_meeting_id(payload))
        transcript_id = str(payload["transcript_id"]) if payload.get("transcript_id") else None
        message_id = str(payload.get("message_id", ""))
        correlation_id = payload.get("correlation_id") or self.repository.pipeline_correlation(job_id, meeting_id)
        if message_id and not self.repository.claim(message_id, job_id):
            return
        current = self.repository.job_state(job_id)
        if current in {"READY", "FAILED", "CANCELLED"}:
            LOGGER.info("skip terminal summary job=%s status=%s", job_id, current)
            self.repository.release_message(message_id)
            return
        LOGGER.info("summary job=%s correlation_id=%s meeting_id=%s", job_id, correlation_id, meeting_id)
        self.repository.update_job(job_id, "RUNNING", "PREPARING_CONTEXT", 5)
        try:
            segments = await asyncio.to_thread(self.repository.load_segments, meeting_id, transcript_id)
            if not segments:
                raise RuntimeError("transcript_has_no_segments")
            self.repository.update_job(job_id, "RUNNING", "EXTRACTING_FACTS", 10)
            LOGGER.info("job=%s waiting for GPU lease", job_id)
            llm_started_at = time.perf_counter()
            preemption_wait_ms = 0.0
            model_start_ms = 0.0
            generation_started_at = llm_started_at
            async with self._gpu_lease:
                LOGGER.info("job=%s acquired GPU lease", job_id)
                preemption_started_at = time.perf_counter()
                if not await asyncio.to_thread(self._gpu_coordination.llm_may_start):
                    raise RuntimeError("gpu_asr_pending")
                preemption_wait_ms = (time.perf_counter() - preemption_started_at) * 1000.0
                model_started_at = time.perf_counter()
                server = await asyncio.to_thread(self._llm_runtime.ensure_started)
                model_start_ms = (time.perf_counter() - model_started_at) * 1000.0
                if not await asyncio.to_thread(self._gpu_coordination.mark_llm_resident, self._llm_owner, "SUMMARY", job_id):
                    await asyncio.to_thread(self._llm_runtime.stop)
                    raise RuntimeError("gpu_asr_pending")
                await asyncio.to_thread(self._gpu_coordination.mark_llm_busy, self._llm_owner, "SUMMARY", job_id)
                try:
                    client = self._client_for(server.base_url)
                    generation_started_at = time.perf_counter()

                    async def report_progress(stage: str, progress: int) -> None:
                        await asyncio.to_thread(self.repository.update_job, job_id, "RUNNING", stage, progress)

                    result = await SummaryOrchestrator(
                        client.invoke_json,
                        profile=payload.get("summary_profile", payload.get("profile")),
                        context=MeetingContext.from_mapping(payload.get("meeting_context", payload.get("context"))),
                        progress=report_progress,
                    ).summarize(segments)
                    profile_name = str(payload.get("summary_profile", payload.get("profile")) or "").upper()
                    result["prompt_version"] = str(
                        payload.get("prompt_version")
                        or (PROTOCOL_RU_PROMPT_VERSION if profile_name == MEETING_PROTOCOL_RU else SUMMARY_PROMPT_VERSION)
                    )
                    if profile_name == MEETING_PROTOCOL_RU:
                        result["profile"] = MEETING_PROTOCOL_RU
                        result["schema_version"] = PROTOCOL_RU_SCHEMA_VERSION
                    if "contentValidity" not in result:
                        quality = result.get("quality") if isinstance(result.get("quality"), dict) else {}
                        needs_review = str(quality.get("status", "")).upper() in {"NEEDS_REVIEW", "FAILED"} or bool(quality.get("review_items"))
                        result["contentValidity"] = "NEEDS_REVIEW" if needs_review else "VALID"
                        result["generationState"] = "READY_WITH_WARNINGS" if needs_review else "READY"
                        result["errorCode"] = None
                    if correlation_id:
                        result["correlation_id"] = correlation_id
                finally:
                    await asyncio.to_thread(self._llm_runtime.release_after_job)
                    if self._llm_runtime.enabled:
                        if not await asyncio.to_thread(self._gpu_coordination.mark_llm_resident, self._llm_owner):
                            await asyncio.to_thread(self._gpu_coordination.preempt_if_requested, self._llm_runtime, self._llm_owner)
                    else:
                        await asyncio.to_thread(self._gpu_coordination.mark_llm_stopped, self._llm_owner)
            LOGGER.info("job=%s released GPU lease", job_id)
            self.repository.update_job(job_id, "RUNNING", "VALIDATING_EVIDENCE", 70)
            self.repository.update_job(job_id, "RUNNING", "PERSISTING", 95)
            persist_started_at = time.perf_counter()
            persisted = await asyncio.to_thread(self.repository.persist, job_id, meeting_id, transcript_id, result, self.model_alias)
            try:
                await asyncio.to_thread(
                    self.repository.record_pipeline_metrics,
                    job_id,
                    meeting_id,
                    {
                        "summary_llm_ms": max(0.0, (time.perf_counter() - llm_started_at) * 1000.0),
                        "summary_preemption_wait_ms": max(0.0, preemption_wait_ms),
                        "summary_model_start_ms": max(0.0, model_start_ms),
                        "summary_generation_ms": max(0.0, (persist_started_at - generation_started_at) * 1000.0),
                        "summary_persist_ms": max(0.0, (time.perf_counter() - persist_started_at) * 1000.0),
                        "summary_total_ms": max(0.0, (time.perf_counter() - started_at) * 1000.0),
                    },
                )
            except Exception:
                LOGGER.warning("summary_pipeline_metrics_persist_failed job=%s", job_id, exc_info=True)
            if not persisted:
                LOGGER.info("summary job=%s result discarded because the meeting was cancelled or deleted", job_id)
                await asyncio.to_thread(self.repository.release_message, message_id)
        except Exception as exc:
            detail = type(exc).__name__ + ": " + str(exc)
            if is_retryable_summary_error(exc):
                scheduled_attempt = self.repository.schedule_retry(
                    job_id, detail, "SUMMARY_RETRY_PENDING", message_id=message_id
                )
                if scheduled_attempt is not None:
                    raise RetryScheduled(retry_delay_seconds(scheduled_attempt)) from exc
            # The failure is terminal and has been durably persisted. Ack the
            # current NATS delivery instead of NAK'ing it: a terminal summary
            # must not make JetStream redeliver the same payload just to learn
            # that the job is already FAILED. mark_failed also removes the
            # inbox lease in the same transaction.
            self.repository.mark_failed(job_id, meeting_id, detail, message_id=message_id)
            LOGGER.error("summary job=%s entered terminal failure: %s", job_id, detail)
            return


async def run() -> None:
    try:
        import nats
    except ImportError as exc:
        raise RuntimeError("Install workers/summary_worker/requirements.txt") from exc
    client = await nats.connect(os.getenv("NATS_URL", "nats://nats:4222"))
    # Capabilities must observe the same process-scoped runtime used by both
    # NATS consumers; a separate probe reports STOPPED while Qwen is resident.
    runtime_owner = f"summary-runtime:{socket.gethostname()}:{os.getpid()}:{os.urandom(6).hex()}"
    shared_coordination = GpuRuntimeCoordinator(owner=runtime_owner)
    shared_runtime = LocalLlamaRuntime()
    embedding_integrity_key: tuple[Any, ...] | None = None
    embedding_integrity_status = "NOT_CONFIGURED"
    def embedding_capabilities() -> dict[str, Any]:
        """Describe the configured retrieval provider without loading a model.

        The heartbeat must stay cheap and deterministic.  The Assistant worker
        performs the actual ONNX session construction lazily; this probe only
        reports whether the immutable snapshot is present and what provider
        will be selected, so readiness never downloads a model or touches CUDA.
        """
        nonlocal embedding_integrity_key, embedding_integrity_status
        requested = os.getenv("ASSISTANT_EMBEDDING_PROVIDER", "auto").strip().lower()
        onnx_path = os.getenv(
            "ASSISTANT_EMBEDDING_ONNX_PATH",
            "/models/embeddings/paraphrase-multilingual-MiniLM-L12-v2.onnx",
        ).strip()
        tokenizer_path = os.getenv(
            "ASSISTANT_EMBEDDING_TOKENIZER_PATH",
            "/models/embeddings/tokenizer.json",
        ).strip()
        onnx_sha256 = os.getenv("ASSISTANT_EMBEDDING_ONNX_SHA256", "").strip()
        tokenizer_sha256 = os.getenv("ASSISTANT_EMBEDDING_TOKENIZER_SHA256", "").strip()
        require_verified = os.getenv("ASSISTANT_EMBEDDING_REQUIRE_VERIFIED", "false").strip().lower() in {"1", "true", "yes"}
        onnx_ready = Path(onnx_path).is_file() and Path(tokenizer_path).is_file()
        explicit_onnx = requested in {"onnx", "onnx-cpu", "onnx_cpu"}
        def stamp(path: str) -> int:
            try:
                return Path(path).stat().st_mtime_ns
            except OSError:
                return 0
        key = (onnx_path, tokenizer_path, onnx_sha256, tokenizer_sha256, onnx_ready,
               stamp(onnx_path) if onnx_ready else 0,
               stamp(tokenizer_path) if onnx_ready else 0)
        if key != embedding_integrity_key:
            embedding_integrity_key = key
            if not onnx_ready:
                embedding_integrity_status = "MISSING"
            elif not (onnx_sha256 and tokenizer_sha256):
                embedding_integrity_status = "NOT_CONFIGURED"
            else:
                try:
                    def digest(path: str) -> str:
                        hasher = hashlib.sha256()
                        with open(path, "rb") as stream:
                            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                                hasher.update(chunk)
                        return hasher.hexdigest().lower()
                    embedding_integrity_status = "VERIFIED" if digest(onnx_path) == onnx_sha256.lower() and digest(tokenizer_path) == tokenizer_sha256.lower() else "MISMATCH"
                except OSError:
                    embedding_integrity_status = "ERROR"
        snapshot_ok = onnx_ready and embedding_integrity_status not in {"MISMATCH", "ERROR"}
        verified_snapshot_ok = snapshot_ok and embedding_integrity_status == "VERIFIED"
        requires_onnx = require_verified and requested not in {"hash", "hashed", "hashed-local-v1"}
        selected = "onnx-cpu:paraphrase-multilingual-MiniLM-L12-v2" if snapshot_ok and (requested == "auto" or explicit_onnx) and (not require_verified or verified_snapshot_ok) else ("unavailable" if requires_onnx else "hashed-local-v1")
        if selected.startswith("onnx-cpu:"):
            reason = "ready"
        elif embedding_integrity_status in {"MISMATCH", "ERROR"}:
            reason = "onnx_snapshot_sha256_mismatch"
        elif require_verified and not verified_snapshot_ok:
            reason = "onnx_snapshot_verification_required"
        elif explicit_onnx:
            reason = "onnx_snapshot_missing"
        elif requested == "auto":
            reason = "onnx_snapshot_missing_using_hashed_fallback"
        else:
            reason = "provider_configured_hashed_fallback"
        model_ready = selected.startswith("onnx-cpu:") or (not require_verified and requested in {"hash", "hashed", "hashed-local-v1"})
        return {
            "embeddingProvider": selected,
            "embeddingModel": "paraphrase-multilingual-MiniLM-L12-v2" if selected.startswith("onnx-cpu:") else None,
            "embeddingDevice": "CPU" if selected.startswith("onnx-cpu:") else "CPU_FALLBACK",
            "embeddingModelReady": model_ready,
            "embeddingOnnxPath": onnx_path if explicit_onnx or onnx_ready else None,
            "embeddingTokenizerPath": tokenizer_path if explicit_onnx or onnx_ready else None,
            "embeddingSnapshotSha256Configured": bool(onnx_sha256 and tokenizer_sha256),
            "embeddingSnapshotIntegrity": embedding_integrity_status,
            "embeddingVerificationRequired": require_verified,
            "embeddingFallbackReason": reason,
        }
    def summary_capabilities() -> dict[str, Any]:
        model_path = os.getenv("LLM_MODEL_PATH", "/models/qwen3-8b/Qwen3-8B-Q5_K_M.gguf")
        manifest_path = os.getenv("LLM_MODEL_MANIFEST", model_path + ".manifest.json")
        llama_binary = os.getenv("LLAMA_RUNTIME_BINARY", "/opt/llama/llama-server")
        model_available = os.path.isfile(model_path) and os.path.getsize(model_path) > 0
        manifest_available = os.path.isfile(manifest_path)
        expected_sha = os.getenv("LLM_MODEL_SHA256", "").strip().upper()
        manifest_sha = ""
        manifest_revision = ""
        manifest_size = None
        manifest_valid = False
        validation_reason = "model_missing" if not model_available else "model_manifest_missing"
        if manifest_available:
            try:
                with open(manifest_path, "r", encoding="utf-8") as manifest_file:
                    manifest = json.load(manifest_file)
                manifest_sha = str(manifest.get("sha256", "")).upper()
                manifest_revision = str(manifest.get("revision", ""))
                manifest_size = int(manifest.get("size", 0))
                size_matches = manifest_size == os.path.getsize(model_path)
                sha_matches = not expected_sha or manifest_sha == expected_sha
                filename_matches = str(manifest.get("filename", "")) == os.path.basename(model_path)
                manifest_valid = bool(manifest.get("schemaVersion") == 1 and manifest_sha and size_matches and sha_matches and filename_matches)
                validation_reason = "ready" if manifest_valid else "model_manifest_mismatch"
            except (OSError, ValueError, TypeError, json.JSONDecodeError):
                validation_reason = "model_manifest_invalid"
        return {
            "modelPath": model_path,
            "manifestPath": manifest_path,
            "modelAvailable": model_available,
            "modelManifestAvailable": manifest_available,
            "modelManifestValid": manifest_valid,
            "modelChecksumExpected": bool(expected_sha),
            "modelExpectedSha256": expected_sha,
            "modelManifestSha256": manifest_sha,
            "modelManifestRevision": manifest_revision,
            "modelManifestSize": manifest_size,
            "modelValidationReason": validation_reason,
            "llamaRuntimeAvailable": os.path.isfile(llama_binary),
            "llamaRuntimeState": shared_runtime.state,
            "llamaResidentEnabled": shared_runtime.enabled,
            "gpuRequired": os.getenv("LLM_REQUIRE_GPU", "true").lower() in {"1", "true", "yes"},
            **embedding_capabilities(),
        }
    heartbeat = AsyncHeartbeat("summary-worker", capabilities=summary_capabilities)
    await heartbeat.start()
    def set_runtime_state() -> None:
        capabilities = summary_capabilities()
        if not capabilities["modelAvailable"] or not capabilities["llamaRuntimeAvailable"]:
            heartbeat.set_state("UNAVAILABLE", capabilities["modelValidationReason"])
        elif os.getenv("ASSISTANT_EMBEDDING_REQUIRE_VERIFIED", "false").strip().lower() in {"1", "true", "yes"} and not capabilities.get("embeddingModelReady"):
            heartbeat.set_state("DEGRADED", "EMBEDDING_MODEL_INVALID")
        elif not capabilities["modelManifestValid"]:
            heartbeat.set_state("DEGRADED", capabilities["modelValidationReason"])
        elif capabilities.get("llamaResidentEnabled") and str(capabilities.get("llamaRuntimeState", "")).upper() in {"FAILED", "UNAVAILABLE"}:
            heartbeat.set_state("UNAVAILABLE", "LLAMA_RUNTIME_FAILED")
        elif capabilities.get("llamaResidentEnabled") and str(capabilities.get("llamaRuntimeState", "")).upper() in {"STARTING", "DEGRADED"}:
            heartbeat.set_state("DEGRADED", "LLAMA_RUNTIME_NOT_READY")
        else:
            heartbeat.set_state("READY")
    jetstream = client.jetstream()
    await ensure_stream(jetstream, name="WHISPERX", subjects=["media.ingest", "ml.transcribe", "llm.summarize", "llm.assistant"])
    summary_subscription = await jetstream.pull_subscribe("llm.summarize", durable="summary-worker")
    assistant_subscription = await jetstream.pull_subscribe("llm.assistant", durable="assistant-worker")
    set_runtime_state()
    # One process-scoped owner and one coordinator/runtime are shared by both
    # NATS consumers. Consumer names remain durable subscription identities,
    # never GPU ownership identities.
    if await asyncio.to_thread(shared_coordination.reclaim_stale_owner):
        LOGGER.warning("reclaimed_stale_llm_runtime_owner")
    summary_worker = SummaryWorker(shared_coordination, shared_runtime, runtime_owner)
    recovered_summary = await asyncio.to_thread(summary_worker.repository.reset_stale_leases)
    if recovered_summary:
        LOGGER.warning("recovered stale summary jobs count=%s", recovered_summary)
    assistant_worker = AssistantWorker(shared_coordination, shared_runtime, runtime_owner)
    assistant_watchdog_interval = max(15.0, float(os.getenv("ASSISTANT_QUEUE_WATCHDOG_INTERVAL_SECONDS", "30")))
    # The durable GPU wait budget is one hour.  Keep the legacy
    # ASSISTANT_QUEUE_TIMEOUT_SECONDS override, but fall back to the public
    # GPU queue setting so the watchdog cannot expire a healthy query after
    # ten minutes while schedule_gpu_wait still permits an hour.
    assistant_queue_timeout = max(
        120,
        int(os.getenv(
            "ASSISTANT_QUEUE_TIMEOUT_SECONDS",
            os.getenv("ASSISTANT_GPU_QUEUE_TIMEOUT_SECONDS", "3600"),
        )),
    )

    async def gpu_coordination_watch() -> None:
        """Unload resident Qwen promptly when an ASR job requests the GPU."""
        heartbeat_interval = max(1.0, float(os.getenv("GPU_LLM_OWNER_HEARTBEAT_SECONDS", "5")))
        poll_interval = max(0.25, float(os.getenv("GPU_COORDINATION_POLL_SECONDS", "1")))
        last_heartbeat = 0.0
        while True:
            now = time.monotonic()
            if now - last_heartbeat >= heartbeat_interval:
                await asyncio.to_thread(shared_coordination.heartbeat_owner, runtime_owner)
                last_heartbeat = now
            await asyncio.to_thread(
                summary_worker._gpu_coordination.preempt_if_requested,
                summary_worker._llm_runtime,
                runtime_owner,
            )
            await asyncio.sleep(poll_interval)

    async def qwen_warmup_watch() -> None:
        """Warm idle Qwen only when the GPU has no active ASR work.

        Warm-up is deliberately best-effort. It never changes the durable
        Assistant retry budget and is stopped immediately after the load if a
        higher-priority ASR/Assistant/V2 request appeared while the process
        was starting.
        """
        if not shared_runtime.enabled or os.getenv("LLM_WARMUP_ENABLED", "true").lower() not in {"1", "true", "yes"}:
            return
        idle_seconds = max(30.0, float(os.getenv("LLM_WARMUP_IDLE_SECONDS", "30")))
        poll_interval = max(2.0, float(os.getenv("LLM_WARMUP_POLL_SECONDS", "5")))
        last_activity = time.monotonic()
        while True:
            await asyncio.sleep(poll_interval)
            if shared_runtime.state != "STOPPED":
                last_activity = time.monotonic()
                continue
            if time.monotonic() - last_activity < idle_seconds:
                continue
            try:
                if await asyncio.to_thread(shared_coordination.higher_priority_work_active):
                    last_activity = time.monotonic()
                    continue
                # Warm-up takes the lowest-priority lease. This prevents it
                # from loading CUDA concurrently with ASR/Assistant/V2 and
                # lets the polling loop cancel a slow model start as soon as
                # higher-priority durable work appears.
                warmup_lease = PostgresGpuLease(shared_coordination.conninfo, priority=100, wait_seconds=1)
                async with warmup_lease:
                    if await asyncio.to_thread(shared_coordination.higher_priority_work_active):
                        last_activity = time.monotonic()
                        continue
                    cancellation = threading.Event()
                    start_task = asyncio.create_task(asyncio.to_thread(shared_runtime.ensure_started, cancellation))
                    try:
                        while not start_task.done():
                            await asyncio.sleep(2.0)
                            if await asyncio.to_thread(shared_coordination.higher_priority_work_active):
                                cancellation.set()
                                break
                        # Shield the thread-backed start from task cancellation;
                        # on shutdown first signal the cooperative event and
                        # wait for the child to terminate so no llama startup
                        # thread survives the worker process lifecycle.
                        await asyncio.shield(start_task)
                    except asyncio.CancelledError:
                        cancellation.set()
                        try:
                            await asyncio.shield(start_task)
                        except (asyncio.CancelledError, RuntimeError):
                            pass
                        raise
                    except RuntimeError as exc:
                        if str(exc) != "llm_warmup_cancelled":
                            raise
                    if await asyncio.to_thread(shared_coordination.higher_priority_work_active):
                        await asyncio.to_thread(shared_runtime.stop)
                        await asyncio.to_thread(shared_coordination.mark_llm_stopped, runtime_owner)
                    else:
                        await asyncio.to_thread(shared_coordination.mark_llm_resident, runtime_owner, "LLM_WARMUP", f"warmup:{os.getpid()}")
                last_activity = time.monotonic()
                set_runtime_state()
            except Exception:
                LOGGER.warning("llm_warmup_failed", exc_info=True)
                last_activity = time.monotonic()

    async def consume_summary() -> None:
        while True:
            await asyncio.to_thread(summary_worker._llm_runtime.release_idle)
            if summary_worker._llm_runtime.state == "STOPPED":
                await asyncio.to_thread(shared_coordination.mark_llm_stopped, runtime_owner)
            set_runtime_state()
            for message in await fetch_available(summary_subscription, nats.errors.TimeoutError, timeout=1):
                job_id: str | None = None
                try:
                    payload = json.loads(message.data)
                except (json.JSONDecodeError, TypeError, UnicodeDecodeError):
                    LOGGER.exception("summary_poison_message_discarded")
                    await message.ack()
                    continue
                if not isinstance(payload, dict) or not str(payload.get("job_id", "")).strip():
                    LOGGER.error("summary_poison_message_discarded reason=job_id_missing")
                    await message.ack()
                    continue
                try:
                    require_meeting_id(payload)
                except ValueError:
                    LOGGER.error("summary_poison_message_discarded reason=meeting_id_missing")
                    await message.ack()
                    continue
                try:
                    job_id = str(payload.get("job_id", ""))
                    message_id = str(payload.get("message_id", ""))
                    heartbeat.set_job(job_id or None)
                    heartbeat.set_state("BUSY")
                    async with maintain_message(message, on_tick=lambda: asyncio.to_thread(summary_worker.repository.renew_lease, job_id, message_id)):
                        await summary_worker.handle(payload)
                    await message.ack()
                except RetryScheduled as exc:
                    LOGGER.warning("summary retry scheduled job=%s delay=%ss", job_id, exc.delay_seconds)
                    await message.nak(delay=exc.delay_seconds)
                except Exception:
                    LOGGER.exception("summary_message_failed job_id=%s", job_id)
                    await message.nak()
                finally:
                    heartbeat.set_job(None)
                    set_runtime_state()

    async def consume_assistant() -> None:
        last_watchdog = 0.0
        while True:
            await asyncio.to_thread(assistant_worker._llm_runtime.release_idle)
            if assistant_worker._llm_runtime.state == "STOPPED":
                await asyncio.to_thread(shared_coordination.mark_llm_stopped, runtime_owner)
            set_runtime_state()
            now = time.monotonic()
            if now - last_watchdog >= assistant_watchdog_interval:
                expired = await asyncio.to_thread(assistant_worker.repository.expire_stale_queries, assistant_queue_timeout)
                if expired:
                    LOGGER.warning("assistant_stale_queries_expired count=%s timeout_seconds=%s", expired, assistant_queue_timeout)
                last_watchdog = now
            for message in await fetch_available(assistant_subscription, nats.errors.TimeoutError, timeout=1):
                query_id: str | None = None
                try:
                    payload = json.loads(message.data)
                except (json.JSONDecodeError, TypeError, UnicodeDecodeError):
                    LOGGER.exception("assistant_poison_message_discarded")
                    await message.ack()
                    continue
                if not isinstance(payload, dict) or not str(payload.get("query_id", "")).strip():
                    LOGGER.error("assistant_poison_message_discarded reason=query_id_missing")
                    await message.ack()
                    continue
                try:
                    query_id = str(payload.get("query_id", ""))
                    message_id = str(payload.get("message_id", ""))
                    heartbeat.set_job(query_id)
                    heartbeat.set_state("BUSY")
                    async with maintain_message(message, on_tick=lambda: asyncio.to_thread(assistant_worker.repository.renew_lease, query_id, message_id)):
                        await assistant_worker.handle(payload)
                    await message.ack()
                except AssistantRetryScheduled as exc:
                    LOGGER.warning(
                        "assistant retry scheduled query_id=%s attempt=%s delay=%ss",
                        query_id,
                        exc.attempt,
                        exc.delay_seconds,
                    )
                    await message.nak(delay=exc.delay_seconds)
                except AssistantGpuWaitScheduled as exc:
                    LOGGER.info("assistant query=%s waiting for GPU retry_in=%ss", query_id, exc.delay_seconds)
                    await message.nak(delay=exc.delay_seconds)
                except AssistantMessageAlreadyClaimed as exc:
                    LOGGER.warning("assistant message claimed by another worker query=%s retry_in=%.1fs", query_id, exc.delay_seconds)
                    await message.nak(delay=exc.delay_seconds)
                except Exception:
                    LOGGER.exception("assistant_message_failed query_id=%s", query_id)
                    await message.nak()
                finally:
                    heartbeat.set_job(None)
                    set_runtime_state()

    coordination_task = asyncio.create_task(gpu_coordination_watch())
    warmup_task = asyncio.create_task(qwen_warmup_watch())
    try:
        await asyncio.gather(consume_assistant(), consume_summary())
    finally:
        coordination_task.cancel()
        warmup_task.cancel()
        await asyncio.gather(coordination_task, warmup_task, return_exceptions=True)
        await asyncio.to_thread(shared_coordination.mark_llm_stopped, runtime_owner)
        await summary_worker.close()
        await assistant_worker.close()

if __name__ == "__main__":
    asyncio.run(run())
