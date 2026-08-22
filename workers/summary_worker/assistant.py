from __future__ import annotations

import asyncio
import json
import logging
import os
import re
import socket
import hashlib
import time
from collections import deque
from difflib import SequenceMatcher
from typing import Any

from psycopg.types.json import Jsonb

from workers.db_pool import DatabaseConnectionPool
from workers.gpu_lease import AssistantGpuBusy, PostgresGpuLease
from workers.gpu_runtime_coordination import GpuRuntimeCoordinator
from .llama_subprocess import LocalLlamaRuntime
from .summarizer import LlamaCppClient
from .hybrid_retrieval import HybridRetriever, RetrievalCandidate
from .answer_planner import assess_requested_fields, build_answer_plan, missing_field_text
from .evidence_reasoner import detect_conflicts
from .evidence_bundles import build_evidence_bundles
from .query_understanding import AssistantQueryPlan, understand_query
from .retrieval_planner import build_retrieval_plan
from workers.memory_worker.memory_retrieval import MemoryQueryPlan, build_memory_query_plan

LOGGER = logging.getLogger("whisperx.assistant-worker")

# This worker shares LocalLlamaRuntime with SummaryWorker.  The coordination
# owner identifies the runtime, so both consumers must use the same value.
LLM_RUNTIME_OWNER = "llm-runtime"

ASSISTANT_MAX_RETRIES = max(0, int(os.getenv("ASSISTANT_MAX_RETRIES", "3")))


class AssistantRetryScheduled(RuntimeError):
    """The current delivery was durably moved back to QUEUED."""

    def __init__(self, delay_seconds: float, attempt: int):
        super().__init__("ASSISTANT_RETRY_SCHEDULED")
        self.delay_seconds = delay_seconds
        self.attempt = attempt


class AssistantGpuWaitScheduled(RuntimeError):
    """The durable Assistant query remains queued behind healthy ASR."""

    def __init__(self, delay_seconds: float = 15.0):
        super().__init__("ASSISTANT_WAITING_FOR_GPU")
        self.delay_seconds = max(5.0, float(delay_seconds))


class AssistantMessageAlreadyClaimed(RuntimeError):
    """A duplicate Assistant delivery is owned by another live consumer."""

    def __init__(self, delay_seconds: float = 15.0):
        super().__init__("assistant_message_claimed_by_active_worker")
        self.delay_seconds = max(5.0, float(delay_seconds))


def assistant_retry_delay_seconds(attempt: int) -> float:
    # Keep retries short enough for an interactive assistant while adding a
    # small deterministic jitter so several requests do not stampede Qwen.
    return {1: 5, 2: 15, 3: 30, 4: 60, 5: 120}.get(attempt, 300) + attempt * 0.37


def is_retryable_assistant_error(exc: BaseException) -> bool:
    """Retry only infrastructure failures; terminal answer failures stay visible."""
    text = f"{type(exc).__name__}: {exc}".lower()
    terminal = (
        "no_evidence", "grounding_rejected", "scope_violation", "invalid_json",
        "response_content_is_not_text", "assistant_query_invalid", "schema_invalid",
        "model_not_found", "invalid_llm_", "gpu_required_but_no_layers",
    )
    if any(token in text for token in terminal):
        return False
    return any(token in text for token in (
        "timeout", "timed out", "connection", "refused", "unavailable", "temporarily",
        "503", "502", "llama", "gpu", "cuda", "nats", "postgres", "psycopg",
        "broken pipe", "server_exit", "lease", "busy",
    ))
# Kept as a compatibility marker for older Desktop/Voice clients. New
# clients receive the explicit NO_EVIDENCE error code below, while rolling
# upgrades may still look for the historical empty-context name.
LEGACY_EMPTY_CONTEXT_ERROR = "assistant_context_empty"
# The original terminal set remains part of the compatibility contract:
# status NOT IN ('READY','FAILED','NEEDS_REVIEW'). New explicit terminal
# states are appended below rather than changing the meaning of old clients.

ASSISTANT_SCHEMA: dict[str, Any] = {
    "type": "object",
    "required": ["answer", "voice_answer", "evidence_segment_ids", "claims"],
    "properties": {
        "answer": {"type": "string", "maxLength": 4000},
        "voice_answer": {"type": "string", "maxLength": 500},
        "evidence_segment_ids": {"type": "array", "maxItems": 8, "items": {"type": "string"}},
        "claims": {
            "type": "array",
            "maxItems": 12,
            "items": {
                "type": "object",
                "required": ["text", "evidenceIds"],
                "properties": {
                    "text": {"type": "string", "maxLength": 1000},
                    "evidenceIds": {"type": "array", "maxItems": 8, "items": {"type": "string"}},
                },
            },
        },
    },
}


def claims_are_structurally_grounded(result: dict[str, Any], valid: dict[str, tuple[str, int, int, str, str, str, int]], assistant_mode: str) -> bool:
    """Fail closed on evidence IDs without trying to judge prose semantics."""
    claims = result.get("claims")
    if assistant_mode == "GENERAL_CHAT":
        return not claims or all(not isinstance(item, dict) or not (item.get("evidenceIds") or []) for item in claims)
    if not isinstance(claims, list) or not claims:
        return False
    for claim in claims:
        if not isinstance(claim, dict) or not str(claim.get("text", "")).strip():
            return False
        ids = claim.get("evidenceIds")
        if not isinstance(ids, list) or not ids or any(str(item).removeprefix("SEG-") not in valid for item in ids):
            return False
    return True


_GROUNDING_STOPWORDS = {
    "это", "этот", "эта", "эти", "что", "как", "кто", "где", "когда", "были", "было",
    "будет", "есть", "для", "при", "или", "и", "в", "во", "на", "по", "из", "с", "со",
    "у", "к", "о", "об", "за", "не", "нет", "да", "так", "мы", "они", "он", "она", "их",
    "его", "её", "может", "можно", "нужно", "решили", "говорили", "сказал", "сказали",
}

# LIVE retrieval must fail closed when a question contains only conversational
# filler (for example "что сейчас решили?").  Without this small second set,
# the fallback to the latest twelve segments could turn unrelated speech into
# apparent evidence. Domain words such as "ремонт", "насос" and names remain
# eligible anchors.
_LIVE_RETRIEVAL_STOPWORDS = _GROUNDING_STOPWORDS | {
    "сейчас", "текущий", "текущая", "текущие", "назвали", "обсудили",
    "решили", "решение", "ответственный", "отвечал", "отвечала", "отвечали",
    "расскажите", "расскажи", "подскажи", "скажите", "пожалуйста",
}

_RU_INFLECTION_SUFFIXES = ("иями", "ами", "ями", "ого", "ему", "ому", "ов", "ев", "ам", "ям", "ах", "ях", "ы", "и", "а", "я", "у", "ю", "е", "о")
_NON_NAME_CAPITALIZED = {
    "Ответ", "Ответственный", "Срок", "Решение", "Решили", "Поручение", "Задача",
    "Итог", "Итоги", "Вопрос", "Причина", "Причины", "Дата", "Нужно", "Нужен",
    "Нужна", "Можно", "Следует", "Поэтому", "Также", "Тогда", "Это", "Этот", "Эта",
}


def _grounding_tokens(value: str) -> set[str]:
    """Normalize harmless Russian inflections without weakening numbers.

    Numeric/date validation remains exact in ``claims_are_semantically_grounded``;
    this helper is used only for prose tokens and treats ё/е and common case
    endings consistently.
    """
    result: set[str] = set()
    for token in re.findall(r"[\wА-Яа-яЁё-]{2,}", (value or "").lower()):
        token = token.replace("ё", "е").strip("-")
        if token and token not in _GROUNDING_STOPWORDS:
            for suffix in _RU_INFLECTION_SUFFIXES:
                if len(token) - len(suffix) >= 4 and token.endswith(suffix):
                    token = token[: -len(suffix)]
                    break
            result.add(token)
    return result


def _named_tokens(value: str) -> set[str]:
    """Return likely proper names/identifiers for strict evidence checks."""
    return {
        token.lower()
        for token in re.findall(r"\b[А-ЯЁ][а-яё]{2,}\b", value or "")
        if token not in _NON_NAME_CAPITALIZED
    }


def claims_are_semantically_grounded(result: dict[str, Any], valid: dict[str, tuple[str, int, int, str, str, str, int]], assistant_mode: str) -> bool:
    """Apply a deterministic second gate to claims before they can be spoken.

    The model is still asked for structured claims, but IDs alone are not
    sufficient: meaningful words and every numeric/date token must occur in
    the cited evidence. This catches fabricated names, amounts and dates
    without requiring another model or changing the database schema.
    """
    if assistant_mode == "GENERAL_CHAT":
        return True
    if not claims_are_structurally_grounded(result, valid, assistant_mode):
        return False
    claims = result.get("claims") or []
    for claim in claims:
        evidence_text = " ".join(
            valid[str(item).removeprefix("SEG-")][3]
            for item in claim.get("evidenceIds", [])
            if str(item).removeprefix("SEG-") in valid
        ).lower()
        claim_text = str(claim.get("text", "")).lower()
        claim_tokens = {token for token in _grounding_tokens(claim_text) if not token.startswith("seg-")}
        evidence_tokens = _grounding_tokens(evidence_text)
        if claim_tokens and not (claim_tokens & evidence_tokens):
            return False
        claim_names = _named_tokens(str(claim.get("text", "")))
        evidence_lower = evidence_text.lower()
        if any(name not in evidence_lower for name in claim_names):
            return False
        numeric_tokens = set(re.findall(r"\d+(?:[.,]\d+)?", claim_text))
        if any(token not in evidence_text for token in numeric_tokens):
            return False
    # The UI answer and shorter spoken answer are a paraphrase channel, not a
    # second claim list. Keep the hard checks for protected values while
    # allowing normal Russian connective words and grammatical paraphrases.
    covered = " ".join(str(claim.get("text", "")) for claim in claims).lower()
    cited = " ".join(
        valid[str(item).removeprefix("SEG-")][3]
        for claim in claims for item in claim.get("evidenceIds", [])
        if str(item).removeprefix("SEG-") in valid
    ).lower()
    for value in (str(result.get("answer", "")), str(result.get("voice_answer", ""))):
        protected = set(re.findall(r"\d+(?:[.,]\d+)?", value))
        protected.update(re.findall(r"\b[\w-]*\d[\w-]*\b", value.lower()))
        protected.update(_named_tokens(value))
        if any(token.lower() not in cited for token in protected):
            return False
    return True


class AssistantRepository:
    def __init__(self) -> None:
        self.conninfo = os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")
        self._db = DatabaseConnectionPool(self.conninfo, "assistant-worker")
        self._hybrid = HybridRetriever()
        self._last_retrieval_metadata: dict[str, Any] = {}
        self._live_provenance: dict[str, tuple[str, str | None, str | None]] = {}
        self._last_query_plan: dict[str, Any] = {}
        self._last_answer_plan: dict[str, Any] = {}

    def close(self) -> None:
        self._db.close()

    def clear_retrieval_metadata(self) -> None:
        self._last_retrieval_metadata = {}
        self._live_provenance = {}
        self._last_query_plan = {}
        self._last_answer_plan = {}

    def set_query_plan(self, plan: dict[str, Any]) -> None:
        self._last_query_plan = dict(plan or {})

    def set_answer_plan(self, plan: dict[str, Any]) -> None:
        self._last_answer_plan = dict(plan or {})

    @property
    def semantic_provider(self) -> Any:
        return self._hybrid.provider

    def conversation_state(self, conversation_id: str | None, user_id: str | None) -> dict[str, Any] | None:
        if not conversation_id or not user_id:
            return None
        try:
            with self._db.connection() as connection:
                row = connection.execute(
                    """SELECT last_intent,last_topic,last_person,last_date_range,last_user_question,last_meeting_id
                       FROM assistant_conversation_state WHERE conversation_id=%s AND user_id=%s""",
                    (conversation_id, user_id),
                ).fetchone()
                if not row:
                    return None
                return {
                    "intent": row[0], "topic": row[1], "person": row[2],
                    "dateRange": row[3], "lastUserQuestion": row[4], "meetingId": str(row[5]) if row[5] else None,
                }
        except Exception:
            # Rolling deployments may run the worker before migration 050.
            # Query understanding is additive and must never make Assistant
            # unavailable when the optional state table is not present.
            return None

    def save_conversation_state(self, conversation_id: str | None, user_id: str | None, meeting_id: str | None, question: str, plan: dict[str, Any]) -> None:
        if not conversation_id or not user_id:
            return
        try:
            with self._db.connection() as connection:
                connection.execute(
                    """INSERT INTO assistant_conversation_state(
                         conversation_id,user_id,last_meeting_id,last_intent,last_topic,last_person,
                         last_date_range,last_user_question,updated_at)
                       VALUES(%s,%s,%s,%s,%s,%s,%s,%s,now())
                       ON CONFLICT(conversation_id) DO UPDATE SET
                         user_id=EXCLUDED.user_id,last_meeting_id=EXCLUDED.last_meeting_id,
                         last_intent=EXCLUDED.last_intent,last_topic=EXCLUDED.last_topic,
                         last_person=EXCLUDED.last_person,last_date_range=EXCLUDED.last_date_range,
                         last_user_question=EXCLUDED.last_user_question,updated_at=now()""",
                    (conversation_id, user_id, meeting_id, plan.get("intent"), plan.get("topic"),
                     plan.get("person"), plan.get("dateRange"), question[:2000]),
                )
        except Exception:
            LOGGER.debug("conversation state persistence unavailable", exc_info=True)

    def claim(self, message_id: str, query_id: str) -> bool:
        with self._db.connection() as connection:
            worker_id = socket.gethostname()
            with connection.transaction():
                existing = connection.execute(
                    "SELECT worker_id,lease_expires_at FROM inbox_messages WHERE message_id=%s FOR UPDATE",
                    (message_id,),
                ).fetchone()
                if existing is not None:
                    owner, lease_expires_at = existing
                    expired = lease_expires_at is None or lease_expires_at.timestamp() <= time.time()
                    if not expired and str(owner or "") != worker_id:
                        remaining = max(15.0, min(60.0, lease_expires_at.timestamp() - time.time() + 0.5))
                        raise AssistantMessageAlreadyClaimed(remaining)
                    row = connection.execute(
                        """UPDATE inbox_messages SET job_id=%s,lease_expires_at=now()+interval '30 minutes',worker_id=%s
                           WHERE message_id=%s RETURNING message_id""",
                        (query_id, worker_id, message_id),
                    ).fetchone()
                    return row is not None
                row = connection.execute(
                    """INSERT INTO inbox_messages(message_id,job_id,lease_expires_at,worker_id)
                       VALUES(%s,%s,now()+interval '30 minutes',%s) RETURNING message_id""",
                    (message_id, query_id, worker_id),
                ).fetchone()
                return row is not None

    def renew_lease(self, query_id: str, message_id: str | None = None) -> None:
        with self._db.connection() as connection:
            if message_id:
                owner = socket.gethostname()
                claimed = connection.execute(
                    "UPDATE inbox_messages SET lease_expires_at=now()+interval '30 minutes',worker_id=%s WHERE message_id=%s AND worker_id=%s RETURNING message_id",
                    (owner, message_id, owner),
                ).fetchone()
                if claimed:
                    connection.execute(
                        "UPDATE assistant_queries SET updated_at=now() WHERE id=%s AND status NOT IN ('READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED','NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED','LLM_UNAVAILABLE')",
                        (query_id,),
                    )
            else:
                connection.execute(
                    "UPDATE assistant_queries SET updated_at=now() WHERE id=%s AND status NOT IN ('READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED','NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED','LLM_UNAVAILABLE')",
                    (query_id,),
                )

    def query(self, query_id: str) -> tuple[str, str | None, str, str | None, str | None, str | None, str, str | None, str | None] | None:
        with self._db.connection() as connection:
            row = connection.execute("SELECT q.query,q.meeting_id,q.status,q.conversation_id,q.user_message_id,q.assistant_message_id,q.assistant_mode,q.user_id,u.role FROM assistant_queries q LEFT JOIN users u ON u.id=q.user_id WHERE q.id=%s", (query_id,)).fetchone()
            return (str(row[0]), str(row[1]) if row[1] else None, str(row[2]), str(row[3]) if row[3] else None, str(row[4]) if row[4] else None, str(row[5]) if row[5] else None, str(row[6] or "MEETING_MEMORY"), str(row[7]) if row[7] else None, str(row[8]) if row[8] else None) if row else None

    def created_at(self, query_id: str) -> Any | None:
        with self._db.connection() as connection:
            row = connection.execute("SELECT created_at FROM assistant_queries WHERE id=%s", (query_id,)).fetchone()
            return row[0] if row else None

    def expire_stale_queries(self, max_age_seconds: int) -> int:
        """Move abandoned queued/running requests to a visible terminal state.

        SSE clients are deliberately bounded, but a request must not remain
        ``QUEUED`` forever when NATS or the worker disappears.  The update is
        idempotent and only touches non-terminal rows whose heartbeat/update
        timestamp is older than the configured safety window.
        """

        max_age_seconds = max(120, int(max_age_seconds))
        with self._db.connection() as connection:
            with connection.transaction():
                rows = connection.execute(
                    """
                    UPDATE assistant_queries
                    SET status='LLM_UNAVAILABLE',
                        error_code='ASSISTANT_QUEUE_TIMEOUT',
                        retryable=false,
                        next_retry_at=NULL,
                        completed_at=now(),
                        updated_at=now()
                    WHERE status IN ('QUEUED','RUNNING')
                      AND updated_at < now() - (%s * interval '1 second')
                    RETURNING id,assistant_message_id
                    """,
                    (max_age_seconds,),
                ).fetchall()
                for _, message_id in rows:
                    if message_id:
                        connection.execute(
                            "UPDATE assistant_messages SET status='LLM_UNAVAILABLE',error_code='ASSISTANT_QUEUE_TIMEOUT',completed_at=now() WHERE id=%s",
                            (message_id,),
                        )
                if rows:
                    connection.execute(
                        "DELETE FROM inbox_messages WHERE job_id = ANY(%s::uuid[])",
                        ([str(row[0]) for row in rows],),
                    )
                return len(rows)

    def set_status(self, query_id: str, status: str, *, error: str | None = None) -> bool:
        with self._db.connection() as connection:
            row = connection.execute("""
                UPDATE assistant_queries
                SET status=%s,error_code=%s,updated_at=now(),
                    next_retry_at=NULL,
                    retryable=CASE WHEN %s IN ('FAILED','LLM_UNAVAILABLE') THEN false ELSE retryable END
                WHERE id=%s AND status NOT IN ('READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED','NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED','LLM_UNAVAILABLE')
                RETURNING id,assistant_message_id
                """, (status, error, status, query_id)).fetchone()
            if row and row[1]:
                connection.execute("UPDATE assistant_messages SET status=%s,error_code=%s WHERE id=%s", (status, error, row[1]))
            return row is not None

    def set_processing_stage(self, query_id: str, stage: str) -> None:
        """Publish a small durable stage without changing query ownership.

        Stage metadata is intentionally additive JSONB.  It lets Desktop show
        a truthful background state after the foreground 15-second wait while
        preserving command/trace/queue metadata from acceptance.
        """
        normalized = (stage or "RUNNING").strip().upper()[:64]
        with self._db.connection() as connection:
            connection.execute(
                """
                UPDATE assistant_queries
                SET answer_metadata=jsonb_set(
                      COALESCE(answer_metadata,'{}'::jsonb),
                      '{processingStage}', to_jsonb(%s::text), true),
                    updated_at=now()
                WHERE id=%s AND status NOT IN ('READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED','NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED','LLM_UNAVAILABLE')
                """,
                (normalized, query_id),
            )

    def schedule_retry(
        self,
        query_id: str,
        error: str,
        message_id: str | None = None,
        max_attempts: int = ASSISTANT_MAX_RETRIES,
    ) -> int | None:
        """Atomically release the inbox claim and schedule one delayed retry."""
        max_attempts = max(0, int(max_attempts))
        with self._db.connection() as connection:
            with connection.transaction():
                row = connection.execute(
                    """
                    UPDATE assistant_queries
                    SET status='QUEUED',
                        error_code='ASSISTANT_RETRY_PENDING',
                        retry_count=retry_count+1,
                        next_retry_at=now() + ((CASE WHEN retry_count=0 THEN 5
                                                     WHEN retry_count=1 THEN 15
                                                     WHEN retry_count=2 THEN 30
                                                     WHEN retry_count=3 THEN 60
                                                     WHEN retry_count=4 THEN 120
                                                     ELSE 300 END) * interval '1 second'),
                        retryable=true,
                        answer_metadata=jsonb_set(
                            COALESCE(answer_metadata,'{}'::jsonb),
                            '{retry}',
                            jsonb_build_object('state','PENDING','error',%s,'updatedAt',now()),
                            true),
                        updated_at=now()
                    WHERE id=%s
                      AND status NOT IN ('READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED','NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED','LLM_UNAVAILABLE')
                      AND retry_count < %s
                    RETURNING retry_count
                    """,
                    (error[:500], query_id, max_attempts),
                ).fetchone()
                if row is not None:
                    connection.execute(
                        "UPDATE assistant_messages SET status='QUEUED',error_code='ASSISTANT_RETRY_PENDING' WHERE id=(SELECT assistant_message_id FROM assistant_queries WHERE id=%s)",
                        (query_id,),
                    )
                    if message_id:
                        connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (message_id,))
            return int(row[0]) if row else None

    def schedule_gpu_wait(self, query_id: str, message_id: str | None = None) -> str:
        """Keep a healthy query durable without consuming retry_count.

        A queue wait is not an infrastructure failure.  It is deliberately
        separate from ``schedule_retry`` so Assistant's three-error retry
        budget remains available for actual Qwen/NATS failures.  The created_at
        bound prevents a request from being refreshed forever by a busy GPU.
        """
        max_wait = max(60, int(os.getenv("ASSISTANT_GPU_QUEUE_TIMEOUT_SECONDS", "3600")))
        delay = max(5, int(os.getenv("ASSISTANT_GPU_WAIT_RETRY_SECONDS", "15")))
        with self._db.connection() as connection:
            with connection.transaction():
                row = connection.execute(
                    """
                    UPDATE assistant_queries
                    SET status='QUEUED', error_code='ASSISTANT_WAITING_FOR_GPU',
                        retryable=true,
                        next_retry_at=now() + (%s * interval '1 second'),
                        answer_metadata=jsonb_set(
                          jsonb_set(COALESCE(answer_metadata,'{}'::jsonb), '{gpuWait}',
                            jsonb_build_object('state','WAITING','nextRetryAt',now()+(%s * interval '1 second')), true),
                          '{processingStage}', to_jsonb('WAITING_FOR_GPU'::text), true),
                        updated_at=now()
                    WHERE id=%s AND created_at >= now() - (%s * interval '1 second')
                      AND status NOT IN ('READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED','NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED','LLM_UNAVAILABLE')
                    RETURNING assistant_message_id
                    """,
                    (delay, delay, query_id, max_wait),
                ).fetchone()
                if row is not None:
                    if message_id:
                        connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (message_id,))
                    if row[0]:
                        connection.execute(
                            "UPDATE assistant_messages SET status='QUEUED',error_code='ASSISTANT_WAITING_FOR_GPU' WHERE id=%s",
                            (row[0],),
                        )
                    return "QUEUED"
                expired = connection.execute(
                    """UPDATE assistant_queries
                       SET status='LLM_UNAVAILABLE',error_code='ASSISTANT_GPU_BUSY_TIMEOUT',retryable=false,next_retry_at=NULL,completed_at=now(),updated_at=now()
                       WHERE id=%s AND status NOT IN ('READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED','NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED','LLM_UNAVAILABLE')
                       RETURNING assistant_message_id""",
                    (query_id,),
                ).fetchone()
                if expired and message_id:
                    connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (message_id,))
                if expired and expired[0]:
                    connection.execute(
                        "UPDATE assistant_messages SET status='LLM_UNAVAILABLE',error_code='ASSISTANT_GPU_BUSY_TIMEOUT',completed_at=now() WHERE id=%s",
                        (expired[0],),
                    )
                return "EXPIRED" if expired else "NOOP"

    def context(self, meeting_id: str | None, query: str = "", owner_user_id: str | None = None, include_all: bool = False, neighbour_window: int = 1) -> tuple[str, dict[str, tuple[str, int, int, str, str, str, int]], str, str | None]:
        """Retrieve scope-safe evidence with Russian FTS + embeddings.

        SQL remains responsible for RBAC, meeting boundaries and transcript
        quality gates.  The local hybrid ranker only sees those rows, adds a
        bounded embedding signal for paraphrases, then expands neighbours in
        the same meeting/transcript before the immutable RETRIEVED snapshot.
        """
        fts_anchor_limit = max(8, min(int(os.getenv("ASSISTANT_FTS_ANCHOR_LIMIT", "64")), 512))
        semantic_candidate_limit = max(32, min(int(os.getenv("ASSISTANT_SEMANTIC_CANDIDATE_LIMIT", "512")), 4096))
        with self._db.connection() as connection:
            rows = connection.execute(
                """
                WITH base AS (
                    SELECT s.id,t.meeting_id,s.start_ms,s.end_ms,t.id AS transcript_id,t.version AS transcript_version,
                           COALESCE(ms.display_name,s.speaker_label,'Спикер N') AS speaker,
                           s.text,t.version_kind,s.ordinal,m.created_at AS meeting_created_at,
                           to_tsvector('russian', COALESCE(s.text,'')) AS search_vector
                    FROM transcript_segments s
                    JOIN transcripts t ON t.id=s.transcript_id
                    LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id
                    JOIN meetings m ON m.id=t.meeting_id
                    WHERE (%s::uuid IS NULL OR t.meeting_id=%s::uuid)
                      AND (%s OR m.owner_id=%s::uuid)
                      AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id)
                      AND t.status IN ('READY','PARTIAL_READY')
                      AND NOT (COALESCE(t.warnings,'[]'::jsonb) ?| ARRAY['ASR_LANGUAGE_MISMATCH','AUDIO_SIGNAL_UNUSABLE','NO_SPEECH_DETECTED'])
                      AND COALESCE(s.is_hidden,false)=false
                      AND (%s::uuid IS NOT NULL OR m.created_at >= now()-interval '90 days')
                ), fts_anchors AS (
                    SELECT base.*,
                           ts_rank_cd(base.search_vector, websearch_to_tsquery('russian', %s)) AS rank
                    FROM base
                    WHERE base.search_vector @@ websearch_to_tsquery('russian', %s)
                    ORDER BY rank DESC,meeting_created_at DESC,meeting_id,ordinal
                    LIMIT %s
                ), semantic_pool AS (
                    SELECT base.*, 0.0::real AS rank
                    FROM base
                    WHERE NOT EXISTS (SELECT 1 FROM fts_anchors anchor WHERE anchor.id=base.id)
                    ORDER BY meeting_created_at DESC,meeting_id,ordinal
                    LIMIT %s
                ), bounded AS (
                    SELECT * FROM fts_anchors
                    UNION ALL
                    SELECT * FROM semantic_pool
                )
                SELECT id,meeting_id,start_ms,end_ms,transcript_id,transcript_version,speaker,text,version_kind,
                       ordinal,rank,meeting_created_at
                FROM bounded
                ORDER BY (rank > 0) DESC,rank DESC,meeting_created_at DESC,meeting_id,ordinal
                """,
                (meeting_id, meeting_id, include_all, owner_user_id, meeting_id, query, query, fts_anchor_limit, semantic_candidate_limit),
            ).fetchall()

            candidates = [
                RetrievalCandidate(
                    segment_id=str(row[0]), meeting_id=str(row[1]), start_ms=int(row[2]), end_ms=int(row[3]),
                    transcript_id=str(row[4]), transcript_version=int(row[5]), speaker=str(row[6]),
                    text=str(row[7] or '').strip(), version_kind=str(row[8] or 'ASR_DRAFT').upper(),
                    ordinal=int(row[9]), fts_rank=float(row[10] or 0.0), meeting_created_at=row[11],
                )
                for row in rows if str(row[7] or '').strip()
            ]
            final_top_k = max(4, min(int(os.getenv("ASSISTANT_FINAL_TOP_K", "12")), 64))
            neighbour_limit = max(final_top_k, min(int(os.getenv("ASSISTANT_NEIGHBOUR_LIMIT", "36")), 128))
            ranked = self._hybrid.rank(query, candidates, limit=final_top_k)
            if meeting_id is None and ranked:
                # History mode is limited to five meetings *after* hybrid
                # ranking, so a paraphrase can still select the relevant one.
                allowed_meetings: list[str] = []
                for item in ranked:
                    value = item.candidate.meeting_id
                    if value not in allowed_meetings and len(allowed_meetings) < 5:
                        allowed_meetings.append(value)
                candidates = [item for item in candidates if item.meeting_id in allowed_meetings]
                ranked = self._hybrid.rank(query, candidates, limit=final_top_k)
            selected = self._hybrid.expand_neighbours(ranked, candidates, limit=neighbour_limit, window=neighbour_window)
            score_by_id = {item.candidate.segment_id: item.score for item in ranked}
            selected.sort(key=lambda item: (-score_by_id.get(item.segment_id, 0.0), item.meeting_id, item.ordinal, item.segment_id))
            rows = [
                (item.segment_id, item.meeting_id, item.start_ms, item.end_ms, item.transcript_id,
                 item.transcript_version, item.speaker, item.text, item.version_kind)
                for item in selected
            ]
            self._last_retrieval_metadata = {
                'method': 'HYBRID_FTS_EMBEDDING',
                'embeddingProvider': self._hybrid.provider.name,
                'candidateCount': len(candidates),
                'ftsCandidateCount': sum(1 for item in candidates if item.fts_rank > 0),
                'anchorCount': len(ranked),
                'selectedCount': len(rows),
                'neighboursIncluded': max(0, len(rows) - len(ranked)),
                'scopeMeetingId': meeting_id,
                'neighbourWindow': max(0, min(int(neighbour_window), 4)),
                'queryPlan': self._last_query_plan or None,
            }
            low_quality = False
            if not rows and meeting_id:
                low_quality = bool(connection.execute(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM transcripts t
                        WHERE t.meeting_id=%s
                          AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id)
                          AND (t.status IN ('PARTIAL_READY','READY') AND (
                               COALESCE(t.warnings,'[]'::jsonb) ?| ARRAY['ASR_LANGUAGE_MISMATCH','AUDIO_SIGNAL_UNUSABLE','NO_SPEECH_DETECTED','SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY']
                               OR COALESCE(t.quality_score,0) < 0.45)))
                    """,
                    (meeting_id,),
                ).fetchone()[0])
        # Assistant prompts use a smaller independent budget than the
        # summary worker.  The default is approximately 8k tokens while the
        # existing override remains available for controlled experiments.
        default_assistant_chars = max(4000, min(36000, int(os.getenv('ASSISTANT_CONTEXT_SIZE', '8192')) * 4))
        max_chars = min(36000, max(4000, int(os.getenv('ASSISTANT_MAX_CONTEXT_CHARS', str(default_assistant_chars)))))
        valid: dict[str, tuple[str, int, int, str, str, str, int]] = {}
        lines: list[str] = []
        kinds: set[str] = set()
        for segment_id, meeting_id_value, start_ms, end_ms, transcript_id, transcript_version, speaker, text, version_kind in rows:
            key = str(segment_id)
            kind = str(version_kind or 'ASR_DRAFT').upper()
            kinds.add(kind)
            valid[key] = (str(meeting_id_value), int(start_ms), int(end_ms), str(text).strip(), kind, str(transcript_id), int(transcript_version))
            lines.append(f'[SEG-{key} {int(start_ms)//1000}s {speaker}] {str(text).strip()}')
        context = '\n'.join(lines)
        if len(context) > max_chars:
            context = context[:max_chars].rsplit('\n', 1)[0]
            allowed = {line.split(' ', 1)[0].removeprefix('[SEG-') for line in context.splitlines()}
            valid = {key: value for key, value in valid.items() if key in allowed}
        return context, valid, ('ASR_DRAFT' if kinds and kinds <= {'ASR_DRAFT', 'V1'} else 'ENRICHED'), ('LOW_TRANSCRIPT_QUALITY' if low_quality else None)

    def context_for_plan(self, meeting_id: str | None, queries: tuple[str, ...], owner_user_id: str | None = None, include_all: bool = False, neighbour_window: int = 1) -> tuple[str, dict[str, tuple[str, int, int, str, str, str, int]], str, str | None]:
        """Retrieve separate evidence bundles for a comparison plan.

        Each query uses the existing scope/RBAC/quality-filtered ``context``
        path.  The merge only de-duplicates segment IDs; it never allows one
        topic to select evidence from another meeting.
        """
        unique_queries = tuple(dict.fromkeys(value.strip() for value in queries if value and value.strip()))
        if len(unique_queries) <= 1:
            return self.context(meeting_id, unique_queries[0] if unique_queries else "", owner_user_id, include_all, neighbour_window)
        contexts: list[str] = []
        merged: dict[str, tuple[str, int, int, str, str, str, int]] = {}
        kinds: set[str] = set()
        reasons: list[str] = []
        metadata_parts: list[dict[str, Any]] = []
        for query in unique_queries[:4]:
            context, valid, transcript_kind, reason = self.context(meeting_id, query, owner_user_id, include_all, neighbour_window)
            if context:
                contexts.append(context)
            merged.update({key: value for key, value in valid.items() if key not in merged})
            kinds.add(transcript_kind)
            if reason:
                reasons.append(reason)
            metadata_parts.append(dict(self._last_retrieval_metadata))
        self._last_retrieval_metadata = {
            "method": "HYBRID_COMPARISON_BUNDLES",
            "embeddingProvider": self._hybrid.provider.name,
            "scopeMeetingId": meeting_id,
            "subQueries": list(unique_queries[:4]),
            "bundleCount": len(metadata_parts),
            "anchorCount": sum(int(item.get("anchorCount", 0) or 0) for item in metadata_parts),
            "candidateCount": sum(int(item.get("candidateCount", 0) or 0) for item in metadata_parts),
            "selectedCount": len(merged),
            "neighbourWindow": max(0, min(int(neighbour_window), 4)),
            "queryPlan": self._last_query_plan or None,
        }
        return "\n".join(contexts), merged, ("ASR_DRAFT" if kinds and kinds <= {"ASR_DRAFT", "V1"} else "ENRICHED"), (reasons[0] if reasons and not merged else None)

    def memory_context(
        self,
        meeting_id: str | None,
        query: str,
        owner_user_id: str | None,
        include_all: bool,
        memory_plan: MemoryQueryPlan,
        neighbour_window: int = 1,
    ) -> tuple[str, dict[str, tuple[str, int, int, str, str, str, int]], str, str | None] | None:
        """Use the memory index only as a bounded canonical-segment locator.

        ``None`` means that the additive memory schema is not available or
        has no matching indexed facts; the caller must use the existing FTS /
        embedding fallback.  Returned context is always rehydrated from
        ``transcript_segments`` after the meeting/RBAC/version gates.
        """
        topic = (memory_plan.topic or "").strip()
        try:
            with self._db.connection() as connection:
                fact_rows = connection.execute(
                    """
                    SELECT f.id,f.meeting_id,f.transcript_id,f.transcript_version,
                           f.evidence_segment_ids,f.fact_type,m.created_at
                    FROM transcript_facts f
                    JOIN meetings m ON m.id=f.meeting_id
                    WHERE f.state='ACTIVE'
                      AND f.fact_type = ANY(%s::text[])
                      AND (%s::uuid IS NULL OR f.meeting_id=%s::uuid)
                      AND (%s OR m.owner_id=%s::uuid)
                      AND (%s='' OR f.subject ILIKE ('%%' || %s || '%%')
                           OR f.value ILIKE ('%%' || %s || '%%')
                           OR EXISTS (
                               SELECT 1 FROM fact_entities fe
                               JOIN memory_entities me ON me.id=fe.entity_id
                               WHERE fe.fact_id=f.id
                                 AND (me.normalized_name ILIKE ('%%' || %s || '%%')
                                      OR me.aliases @> to_jsonb(%s::text))
                           ))
                      AND (%s OR NOT EXISTS (
                           SELECT 1 FROM memory_fact_relations r
                           WHERE r.source_fact_id=f.id
                             AND r.relation_type='SUPERSEDES'
                             AND r.invalidated_at IS NULL))
                    ORDER BY m.created_at DESC,f.start_ms DESC,f.id
                    LIMIT 128
                    """,
                    (
                        list(memory_plan.fact_types), meeting_id, meeting_id,
                        include_all, owner_user_id, topic, topic, topic, topic,
                        topic, memory_plan.include_superseded,
                    ),
                ).fetchall()
                if not fact_rows:
                    return None
                segment_ids: list[str] = []
                for row in fact_rows:
                    raw_ids = row[4]
                    if isinstance(raw_ids, str):
                        try:
                            raw_ids = json.loads(raw_ids)
                        except json.JSONDecodeError:
                            raw_ids = []
                    if isinstance(raw_ids, list):
                        segment_ids.extend(str(value) for value in raw_ids if value)
                segment_ids = list(dict.fromkeys(segment_ids))[:128]
                if not segment_ids:
                    return None
                rows = connection.execute(
                    """
                    SELECT s.id,t.meeting_id,s.start_ms,s.end_ms,t.id AS transcript_id,
                           t.version AS transcript_version,
                           COALESCE(ms.display_name,s.speaker_label,'Спикер N') AS speaker,
                           s.text,t.version_kind,s.ordinal
                    FROM transcript_segments s
                    JOIN transcripts t ON t.id=s.transcript_id
                    JOIN meetings m ON m.id=t.meeting_id
                    LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id
                    WHERE s.id = ANY(%s::uuid[])
                      AND (%s::uuid IS NULL OR t.meeting_id=%s::uuid)
                      AND (%s OR m.owner_id=%s::uuid)
                      AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id)
                      AND t.status IN ('READY','PARTIAL_READY')
                      AND COALESCE(s.is_hidden,false)=false
                    ORDER BY m.created_at DESC,t.meeting_id,t.version,s.ordinal
                    LIMIT 64
                    """,
                    (segment_ids, meeting_id, meeting_id, include_all, owner_user_id),
                ).fetchall()
                if not rows:
                    return None
        except Exception:
            # Migrations 051–055 are additive. A rolling worker must continue
            # using the proven hybrid transcript retrieval until they exist.
            LOGGER.debug("memory index unavailable; using transcript fallback", exc_info=True)
            return None

        valid: dict[str, tuple[str, int, int, str, str, str, int]] = {}
        lines: list[str] = []
        for row in rows:
            segment_id = str(row[0])
            valid[segment_id] = (
                str(row[1]), int(row[2]), int(row[3]), str(row[7] or "").strip(),
                str(row[8] or "ASR_DRAFT").upper(), str(row[4]), int(row[5]),
            )
            if valid[segment_id][3]:
                lines.append(f"[SEG-{segment_id} {int(row[2])//1000}s {row[6]}] {valid[segment_id][3]}")
        if not valid:
            return None
        self._last_retrieval_metadata = {
            "method": "MEMORY_INDEX_CANONICAL_REHYDRATION",
            "factTypes": list(memory_plan.fact_types),
            "temporalMode": memory_plan.temporal_mode,
            "includeSuperseded": memory_plan.include_superseded,
            "candidateCount": len(fact_rows),
            "selectedCount": len(valid),
            "anchorCount": len(valid),
            "scopeMeetingId": meeting_id,
            "neighbourWindow": max(0, min(int(neighbour_window), 4)),
            "canonicalEvidence": True,
        }
        transcript_kind = "ASR_DRAFT" if all(value[4] in {"ASR_DRAFT", "V1"} for value in valid.values()) else "ENRICHED"
        return "\n".join(lines), valid, transcript_kind, None

    def _legacy_fts_context(self, meeting_id: str | None, query: str = "", owner_user_id: str | None = None, include_all: bool = False) -> tuple[str, dict[str, tuple[str, int, int, str, str, str, int]], str, str | None]:
        # Retrieval is performed by PostgreSQL's Russian FTS instead of
        # loading an entire meeting and scoring it in Python. The CTE keeps
        # the twelve strongest hits and adds one neighbouring segment on each
        # side, while the outer limit protects the model prompt.
        with self._db.connection() as connection:
            rows = connection.execute(
                """
                WITH source AS (
                    SELECT s.id,t.meeting_id,s.start_ms,s.end_ms,t.id AS transcript_id,t.version AS transcript_version,
                           COALESCE(ms.display_name,s.speaker_label,'Спикер N') AS speaker,
                           s.text,t.version_kind,s.ordinal,
                           to_tsvector('russian', COALESCE(s.text,'')) AS search_vector,
                           ts_rank_cd(to_tsvector('russian', COALESCE(s.text,'')), websearch_to_tsquery('russian', %s)) AS rank
                    FROM transcript_segments s
                    JOIN transcripts t ON t.id=s.transcript_id
                    LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id
                    JOIN meetings m ON m.id=t.meeting_id
                    WHERE (%s::uuid IS NULL OR t.meeting_id=%s::uuid)
                      AND (%s OR m.owner_id=%s::uuid)
                      AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id)
                      AND t.status IN ('READY','PARTIAL_READY')
                      AND NOT (COALESCE(t.warnings,'[]'::jsonb) ?| ARRAY['ASR_LANGUAGE_MISMATCH','AUDIO_SIGNAL_UNUSABLE','NO_SPEECH_DETECTED'])
                      AND COALESCE(s.is_hidden,false)=false
                      AND (%s::uuid IS NOT NULL OR m.created_at >= now()-interval '90 days')
                ), hits AS (
                    SELECT meeting_id,ordinal FROM source
                    WHERE search_vector @@ websearch_to_tsquery('russian', %s)
                    ORDER BY rank DESC, meeting_id, ordinal
                    LIMIT 12
                ), expanded AS (
                    SELECT DISTINCT source.*
                    FROM source JOIN hits
                      ON hits.meeting_id=source.meeting_id
                     AND source.ordinal BETWEEN hits.ordinal-1 AND hits.ordinal+1
                )
                SELECT id,meeting_id,start_ms,end_ms,transcript_id,transcript_version,speaker,text,version_kind
                FROM expanded
                ORDER BY rank DESC,meeting_id,ordinal
                LIMIT 36
                """,
                (query, meeting_id, meeting_id, include_all, owner_user_id, meeting_id, query),
            ).fetchall()
            low_quality = False
            if not rows and meeting_id:
                low_quality = bool(connection.execute(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM transcripts t
                        WHERE t.meeting_id=%s
                          AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id)
                          AND (t.status IN ('PARTIAL_READY','READY') AND (
                               COALESCE(t.warnings,'[]'::jsonb) ?| ARRAY['ASR_LANGUAGE_MISMATCH','AUDIO_SIGNAL_UNUSABLE','NO_SPEECH_DETECTED','SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY']
                               OR COALESCE(t.quality_score,0) < 0.45)))
                    """,
                    (meeting_id,),
                ).fetchone()[0])
        default_assistant_chars = max(4000, min(36000, int(os.getenv("ASSISTANT_CONTEXT_SIZE", "8192")) * 4))
        max_chars = min(36000, max(4000, int(os.getenv("ASSISTANT_MAX_CONTEXT_CHARS", str(default_assistant_chars)))))
        if rows:
            if meeting_id is None:
                # Rows are already ordered by FTS rank. Keep at most five
                # meetings in history mode, preserving each meeting's local
                # neighbours.
                allowed_meetings: list[str] = []
                for row in rows:
                    key = str(row[1])
                    if key not in allowed_meetings and len(allowed_meetings) < 5:
                        allowed_meetings.append(key)
                rows = [row for row in rows if str(row[1]) in allowed_meetings]

        valid: dict[str, tuple[str, int, int, str, str, str, int]] = {}
        lines: list[str] = []
        kinds: set[str] = set()
        for segment_id, meeting_id_value, start_ms, end_ms, transcript_id, transcript_version, speaker, text, version_kind in rows:
            key = str(segment_id)
            kind = str(version_kind or "ASR_DRAFT").upper()
            kinds.add(kind)
            valid[key] = (str(meeting_id_value), int(start_ms), int(end_ms), str(text).strip(), kind, str(transcript_id), int(transcript_version))
            lines.append(f"[SEG-{key} {int(start_ms)//1000}s {speaker}] {str(text).strip()}")
        context = "\n".join(lines)
        if len(context) > max_chars:
            context = context[:max_chars].rsplit("\n", 1)[0]
            allowed = {line.split(" ", 1)[0].removeprefix("[SEG-") for line in context.splitlines()}
            valid = {key: value for key, value in valid.items() if key in allowed}
        return context, valid, ("ASR_DRAFT" if kinds and kinds <= {"ASR_DRAFT", "V1"} else "ENRICHED"), ("LOW_TRANSCRIPT_QUALITY" if low_quality else None)

    def live_context(self, meeting_id: str | None, query: str = "") -> tuple[str, dict[str, tuple[str, int, int, str, str, str, int]], str, str | None]:
        """Read provisional segments for LIVE_MEETING through the V1 hand-off.

        The rows remain separate from canonical transcripts while a recording
        is active and while the session is FINALIZING. Once a usable V1 exists
        the API cleanup path retires them, so this context can never replace
        V1/V2. The database query still uses only text/timing/provenance.
        """
        self._last_retrieval_metadata = {
            "method": "LIVE_PROVISIONAL_LEXICAL",
            "embeddingProvider": None,
            "scopeMeetingId": meeting_id,
        }
        self._live_provenance = {}
        if not meeting_id:
            return "", {}, "LIVE_PROVISIONAL", "LIVE_MEETING_NOT_READY"
        with self._db.connection() as connection:
            rows = connection.execute(
                """
                SELECT l.id,l.meeting_id,l.start_ms,l.end_ms,l.text,l.confidence,l.source_track_type,l.source_track_id,l.channel_role
                FROM live_meeting_segments l
                JOIN recording_sessions r ON r.id=l.recording_session_id
                WHERE l.meeting_id=%s AND l.expires_at>now()
                  AND r.state IN ('RECORDING','PAUSED','STARTING','AWAITING_AGENT_RECONNECT','FINALIZING')
                  AND NOT EXISTS (
                      SELECT 1 FROM transcripts t
                      WHERE t.meeting_id=l.meeting_id
                        AND t.version=1
                        AND t.status IN ('READY','PARTIAL_READY')
                  )
                ORDER BY start_ms,id
                LIMIT 8192
                """,
                (meeting_id,),
            ).fetchall()
        if not rows:
            return "", {}, "LIVE_PROVISIONAL", "LIVE_MEETING_NOT_READY"
        # A remote speaker is often also audible through the room microphone.
        # Keep one evidence row for a near-identical utterance, preferring the
        # direct render-loopback source. This only affects provisional memory;
        # canonical V1/V2 still process both original tracks independently.
        original_live_count = len(rows)
        deduped: list[tuple[Any, ...]] = []
        normalized_by_index: dict[int, str] = {}
        window: deque[tuple[int, int]] = deque()
        for row in rows:
            text = str(row[4]).strip()
            normalized = re.sub(r"[^\wА-Яа-яЁё]+", " ", text.lower().replace("ё", "е")).strip()
            duplicate_index = None
            start_ms = int(row[2])
            while window and start_ms - window[0][1] > 1500:
                window.popleft()
            for index, _previous_start in window:
                previous_text = normalized_by_index.get(index, "")
                if normalized and previous_text and SequenceMatcher(None, normalized, previous_text).ratio() >= 0.85:
                    duplicate_index = index
                    break
            if duplicate_index is None:
                duplicate_index = len(deduped)
                deduped.append(row)
                normalized_by_index[duplicate_index] = normalized
                window.append((duplicate_index, start_ms))
            elif str(row[8] or "") == "REMOTE_SYSTEM" and str(deduped[duplicate_index][8] or "") != "REMOTE_SYSTEM":
                deduped[duplicate_index] = row
                normalized_by_index[duplicate_index] = normalized
        rows = deduped
        self._last_retrieval_metadata["deduplicatedSegments"] = max(0, original_live_count - len(deduped))
        query_tokens = {
            token for token in re.findall(r"[\wА-Яа-яЁё-]{2,}", (query or "").lower().replace("ё", "е"))
            if token not in _LIVE_RETRIEVAL_STOPWORDS
        }
        scored: list[tuple[int, int, tuple[Any, ...]]] = []
        for index, row in enumerate(rows):
            text = str(row[4]).strip()
            tokens = {
                token for token in re.findall(r"[\wА-Яа-яЁё-]{2,}", text.lower().replace("ё", "е"))
                if token not in _LIVE_RETRIEVAL_STOPWORDS
            }
            scored.append((len(query_tokens & tokens), index, row))
        # Keep the strongest twelve hits and their immediate neighbours so a
        # sentence split across provisional windows is not lost.
        anchors = [index for score, index, _ in sorted(scored, key=lambda item: (-item[0], item[1]))[:12] if score > 0]
        if not anchors:
            # Do not use the most recent speech as a substitute for evidence.
            # LIVE_MEETING is deliberately less capable than final retrieval,
            # but it must remain honest when the question has no lexical
            # connection to the provisional window.
            return "", {}, "LIVE_PROVISIONAL", "LIVE_MEETING_NOT_READY"
        selected = {index for anchor in anchors for index in (anchor - 1, anchor, anchor + 1) if 0 <= index < len(rows)}
        selected_rows = [rows[index] for index in sorted(selected)][:36]
        valid: dict[str, tuple[str, int, int, str, str, str, int]] = {}
        lines: list[str] = []
        for row in selected_rows:
            segment_id, meeting_value, start_ms, end_ms, text, _confidence, source_track_type, source_track_id, channel_role = row
            key = str(segment_id)
            self._live_provenance[key] = (str(source_track_type or "room-microphone"), str(source_track_id) if source_track_id else None, str(channel_role or "LOCAL_ROOM"))
            valid[key] = (str(meeting_value), int(start_ms), int(end_ms), str(text).strip(), "LIVE_PROVISIONAL", "", 0)
            source_label = "удалённый звук" if channel_role == "REMOTE_SYSTEM" else "микрофон помещения"
            lines.append(f"[SEG-{key} {int(start_ms)//1000}s {source_label}] {str(text).strip()}")
        context = "\n".join(lines)
        if len(context) > 36000:
            context = context[:36000].rsplit("\n", 1)[0]
            allowed = {line.split(" ", 1)[0].removeprefix("[SEG-") for line in context.splitlines()}
            valid = {key: value for key, value in valid.items() if key in allowed}
        return context, valid, "LIVE_PROVISIONAL", None

    def history(self, conversation_id: str | None, current_user_message_id: str | None) -> list[dict[str, str]]:
        if not conversation_id:
            return []
        max_messages = max(2, int(os.getenv("ASSISTANT_MAX_HISTORY_MESSAGES", "12")))
        max_chars = max(1000, int(os.getenv("ASSISTANT_MAX_HISTORY_CHARS", "12000")))
        with self._db.connection() as connection:
            rows = connection.execute(
                """
                SELECT role,content FROM assistant_messages
                WHERE conversation_id=%s AND id<>%s AND content<>''
                ORDER BY created_at DESC,id DESC LIMIT %s
                """,
                (conversation_id, current_user_message_id or "00000000-0000-0000-0000-000000000000", max_messages),
            ).fetchall()
        result: list[dict[str, str]] = []
        used = 0
        for role, content in reversed(rows):
            value = str(content).strip()
            if not value or used + len(value) > max_chars:
                continue
            result.append({"role": "user" if role == "USER" else "assistant", "content": value})
            used += len(value)
        return result

    def snapshot_evidence(self, query_id: str, valid: dict[str, tuple[str, int, int, str, str, str, int]]) -> None:
        """Persist the exact prompt evidence before Qwen sees it."""
        if not valid:
            return
        with self._db.connection() as connection:
            with connection.transaction():
                for rank, (segment_id, value) in enumerate(valid.items(), start=1):
                    meeting_id, start_ms, end_ms, text, _, transcript_id, transcript_version = value
                    if value[4] == "LIVE_PROVISIONAL":
                        source_type, source_id, channel_role = self._live_provenance.get(segment_id, ("room-microphone", None, "LOCAL_ROOM"))
                        connection.execute(
                            """INSERT INTO assistant_live_query_evidence(query_id,live_segment_id,rank,snapshot_kind,meeting_id,start_ms,end_ms,text_sha256,source_track_type,source_track_id,channel_role)
                               VALUES(%s,%s,%s,'RETRIEVED',%s,%s,%s,%s,%s,%s,%s)
                               ON CONFLICT(query_id,live_segment_id,snapshot_kind) DO NOTHING""",
                            (query_id, segment_id, rank, meeting_id, start_ms, end_ms, hashlib.sha256(text.encode("utf-8")).hexdigest(), source_type, source_id, channel_role),
                        )
                    else:
                        connection.execute(
                            """INSERT INTO assistant_query_evidence(query_id,segment_id,rank,transcript_id,transcript_version,snapshot_kind,meeting_id,start_ms,end_ms,text_sha256)
                               VALUES(%s,%s,%s,%s,%s,'RETRIEVED',%s,%s,%s,%s)
                               ON CONFLICT(query_id,segment_id,snapshot_kind) DO NOTHING""",
                            (query_id, segment_id, rank, transcript_id, transcript_version, meeting_id, start_ms, end_ms, hashlib.sha256(text.encode("utf-8")).hexdigest()),
                        )

    def persist(self, query_id: str, result: dict[str, Any], valid: dict[str, tuple[str, int, int, str, str, str, int]], assistant_mode: str, transcript_kind: str = "ENRICHED", reason: str | None = None, expected_meeting_id: str | None = None, timings: dict[str, float] | None = None) -> None:
        if assistant_mode in {"CURRENT_MEETING", "LIVE_MEETING"} and expected_meeting_id:
            if any(value[0] != expected_meeting_id for value in valid.values()):
                valid = {}
                reason = "ASSISTANT_SCOPE_VIOLATION"
        claims = result.get("claims") if isinstance(result.get("claims"), list) else []
        claim_ids = [str(item).removeprefix("SEG-") for claim in claims if isinstance(claim, dict) for item in (claim.get("evidenceIds") or [])]
        evidence_ids = claim_ids or [str(value).removeprefix("SEG-") for value in result.get("evidence_segment_ids", [])]
        evidence_ids = list(dict.fromkeys(value for value in evidence_ids if value in valid))[:8]
        query_plan = AssistantQueryPlan.from_mapping(self._last_query_plan)
        supported_fields, missing_fields = ([], [])
        if query_plan and assistant_mode != "GENERAL_CHAT":
            supported_fields, missing_fields = assess_requested_fields(query_plan, valid, evidence_ids)
        answer = str(result.get("answer", "")).strip()
        voice = str(result.get("voice_answer", answer)).strip()
        voice = re.split(r"(?<=[.!?])\s+", voice)
        voice = " ".join(voice[:3])[:500].strip()
        # A meeting answer without immutable segment evidence is not a valid
        # answer. Keep the query visible to Desktop/Voice, but make the
        # missing-grounding state explicit so it cannot be spoken as fact or
        # feed an automatic summary.
        if assistant_mode == "GENERAL_CHAT":
            status = "READY"
            grounding_status = "GROUNDED"
            error_code = None
        elif reason == "ASSISTANT_SCOPE_VIOLATION":
            status = "GROUNDING_REJECTED"
            grounding_status = "REJECTED"
            error_code = "ASSISTANT_SCOPE_VIOLATION"
            answer = "Не удалось подтвердить принадлежность источников текущему совещанию."
            voice = "Источники ответа не относятся к текущему совещанию."
        elif reason == "LOW_TRANSCRIPT_QUALITY":
            status = "NEEDS_REVIEW"
            grounding_status = "WARNING"
            error_code = "LOW_TRANSCRIPT_QUALITY"
            answer = "Эта стенограмма требует проверки качества перед ответом."
            voice = "Сначала проверьте качество стенограммы."
        elif reason == "LIVE_MEETING_NOT_READY":
            status = "NO_EVIDENCE"
            grounding_status = "NO_EVIDENCE"
            error_code = "LIVE_MEETING_NOT_READY"
            answer = "Свежий фрагмент текущего совещания пока не распознан."
            voice = "Пока нет свежего фрагмента совещания для ответа."
        elif evidence_ids and answer and claims_are_semantically_grounded(result, valid, assistant_mode):
            provisional = transcript_kind in {"ASR_DRAFT", "LIVE_PROVISIONAL"}
            partial = bool(supported_fields and missing_fields)
            status = "ANSWERED_WITH_WARNING" if provisional or partial else "READY"
            grounding_status = "WARNING" if provisional or partial else "GROUNDED"
            error_code = None
            # Speak only text that has already passed the claim/evidence gate.
            # This prevents a separate, ungrounded voice channel while still
            # allowing the UI answer to use natural paraphrasing.
            claim_voice = [str(claim.get("text", "")).strip() for claim in claims if isinstance(claim, dict) and str(claim.get("text", "")).strip()]
            if assistant_mode != "GENERAL_CHAT" and claim_voice:
                voice = " ".join(claim_voice[:3])[:500].strip()
            if partial:
                suffix = " ".join(missing_field_text(field) for field in missing_fields[:2])
                answer = f"{answer} {suffix}".strip()
                voice = " ".join(re.split(r"(?<=[.!?])\s+", f"{voice} {suffix}".strip())[:3])[:500].strip()
        elif evidence_ids and answer:
            status = "GROUNDING_REJECTED"
            grounding_status = "REJECTED"
            error_code = "GROUNDING_REJECTED"
            answer = "Не удалось подтвердить ответ по сегментам совещания."
            voice = "Не удалось подтвердить ответ по стенограмме."
        else:
            status = "NO_EVIDENCE"
            grounding_status = "NO_EVIDENCE"
            error_code = "NO_EVIDENCE"
            if not answer:
                answer = "В доступной стенограмме не найден подтверждённый ответ."
            voice = "В стенограмме не найден подтверждённый ответ."
        evidence = []
        for value in evidence_ids:
            item = {"meetingId": valid[value][0], "segmentId": value, "startMs": valid[value][1], "endMs": valid[value][2], "transcriptVersionKind": valid[value][4]}
            if valid[value][4] == "LIVE_PROVISIONAL":
                source_type, source_id, channel_role = self._live_provenance.get(value, ("room-microphone", None, "LOCAL_ROOM"))
                item.update({"sourceTrackType": source_type, "sourceTrackId": source_id, "channelRole": channel_role})
            evidence.append(item)
        with self._db.connection() as connection:
            answer_type = "PARTIAL" if supported_fields and missing_fields else (self._last_answer_plan.get("answerType") or "DIRECT_FACT")
            metadata = {"evidenceCount": len(evidence_ids), "assistantMode": assistant_mode, "transcriptKind": transcript_kind, "provisional": transcript_kind == "LIVE_PROVISIONAL", "canonicalTranscript": transcript_kind != "LIVE_PROVISIONAL", "claimsValidated": status in {"READY", "ANSWERED", "ANSWERED_WITH_WARNING"}, "retrieval": self._last_retrieval_metadata if assistant_mode != "GENERAL_CHAT" else None, "queryPlan": self._last_query_plan or None, "answerPlan": self._last_answer_plan or None, "answerType": answer_type, "supportedFields": supported_fields, "missingFields": missing_fields, "sourceRanges": [{"meetingId": valid[value][0], "segmentId": value, "startMs": valid[value][1], "endMs": valid[value][2], "transcriptVersion": valid[value][6]} for value in evidence_ids], "processingStage": "READY" if status in {"READY", "ANSWERED", "ANSWERED_WITH_WARNING"} else status, "legacyErrorCode": LEGACY_EMPTY_CONTEXT_ERROR if error_code == "NO_EVIDENCE" else None}
            row = connection.execute(
                """UPDATE assistant_queries
                   SET status=%s,answer=%s,voice_answer=%s,evidence=%s::jsonb,error_code=%s,grounding_status=%s,
                       answer_metadata=jsonb_set(COALESCE(answer_metadata,'{}'::jsonb),'{timings}',
                         COALESCE(answer_metadata->'timings','{}'::jsonb) || %s::jsonb,true)
                         || %s::jsonb,
                       completed_at=now(),updated_at=now(),next_retry_at=NULL,retryable=false
                   WHERE id=%s AND status NOT IN ('READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED','NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED','LLM_UNAVAILABLE')
                   RETURNING assistant_message_id,conversation_id""",
                (status, answer, voice, Jsonb(evidence), error_code, grounding_status, Jsonb(timings or {}), Jsonb(metadata), query_id),
            ).fetchone()
            if evidence_ids:
                for rank, segment_id in enumerate(evidence_ids, start=1):
                    meeting_value, start_ms, end_ms, text, _, transcript_id, transcript_version = valid[segment_id]
                    if valid[segment_id][4] == "LIVE_PROVISIONAL":
                        source_type, source_id, channel_role = self._live_provenance.get(segment_id, ("room-microphone", None, "LOCAL_ROOM"))
                        connection.execute(
                            """INSERT INTO assistant_live_query_evidence(query_id,live_segment_id,rank,snapshot_kind,meeting_id,start_ms,end_ms,text_sha256,source_track_type,source_track_id,channel_role)
                               VALUES(%s,%s,%s,'CITED',%s,%s,%s,%s,%s,%s,%s)
                               ON CONFLICT(query_id,live_segment_id,snapshot_kind) DO NOTHING""",
                            (query_id, segment_id, rank, meeting_value, start_ms, end_ms, hashlib.sha256(text.encode("utf-8")).hexdigest(), source_type, source_id, channel_role),
                        )
                    else:
                        connection.execute(
                            """INSERT INTO assistant_query_evidence(query_id,segment_id,rank,transcript_id,transcript_version,snapshot_kind,meeting_id,start_ms,end_ms,text_sha256)
                               VALUES(%s,%s,%s,%s,%s,'CITED',%s,%s,%s,%s)
                               ON CONFLICT(query_id,segment_id,snapshot_kind) DO NOTHING""",
                            (query_id, segment_id, rank, transcript_id, transcript_version, meeting_value, start_ms, end_ms, hashlib.sha256(text.encode("utf-8")).hexdigest()),
                        )
            if row and row[0]:
                connection.execute(
                    "UPDATE assistant_messages SET status=%s,content=%s,voice_answer=%s,evidence=%s::jsonb,error_code=%s,completed_at=now() WHERE id=%s",
                    (status, answer, voice, Jsonb(evidence), error_code, row[0]),
                )
            if row and row[1]:
                connection.execute("UPDATE assistant_conversations SET updated_at=now() WHERE id=%s", (row[1],))


class AssistantWorker:
    def __init__(self, gpu_coordination: GpuRuntimeCoordinator | None = None,
                 llm_runtime: LocalLlamaRuntime | None = None,
                 llm_owner: str | None = None) -> None:
        self.repository = AssistantRepository()
        # Assistant is interactive and must wait behind V1 ASR, but ahead of
        # optional enrichment and automatic Summary work.
        self.lease = PostgresGpuLease(self.repository.conninfo, priority=30)
        self._gpu_coordination = gpu_coordination or GpuRuntimeCoordinator(self.repository.conninfo)
        self._llm_owner = llm_owner or self._gpu_coordination.owner
        self.model_alias = os.getenv("LLM_MODEL_ALIAS", "qwen3-8b")
        self._llm_runtime = llm_runtime or LocalLlamaRuntime()
        self._llm_client: LlamaCppClient | None = None

    def _client_for(self, base_url: str) -> LlamaCppClient:
        normalized = base_url.rstrip("/")
        if self._llm_client is None or not self._llm_client.url.startswith(normalized):
            self._llm_client = LlamaCppClient(normalized, self.model_alias)
        return self._llm_client

    @staticmethod
    def _retrieval_query_for_follow_up(query: str, history: list[dict[str, str]]) -> str:
        """Use the previous user turn as evidence key for a modifier.

        ``history`` is conversation context, not evidence. Only the previous
        USER question is reused for transcript retrieval; assistant answers
        are deliberately ignored so a prior hallucination cannot become a
        new source of facts.
        """
        normalized = re.sub(r"\s+", " ", (query or "").strip().lower().replace("ё", "е"))
        modifiers = {
            "повтори",
            "повтори ответ",
            "повтори последний ответ",
            "короче",
            "ответь короче",
            "коротко",
            "сделай предыдущий ответ короче",
            "подробнее",
            "расскажи подробнее",
            "объясни подробнее",
            "расскажи подробнее по предыдущему ответу",
            "вернись к предыдущему вопросу",
            "повтори предыдущий вопрос",
            "вернись к предыдущему вопросу и ответь на него снова",
        }
        if normalized not in modifiers:
            return query
        for message in reversed(history or []):
            if str(message.get("role", "")).lower() == "user":
                previous = str(message.get("content", "")).strip()
                if previous:
                    return previous
        return query

    async def close(self) -> None:
        if self._llm_client is not None:
            await self._llm_client.aclose()
            self._llm_client = None
        self.repository.close()

    async def handle(self, payload: dict[str, Any]) -> None:
        total_started = time.perf_counter()
        query_id = str(payload["query_id"])
        message_id = str(payload.get("message_id", ""))
        if message_id and not self.repository.claim(message_id, query_id):
            return
        row = self.repository.query(query_id)
        if row is None or row[2] in {"READY", "ANSWERED", "ANSWERED_WITH_WARNING", "FAILED", "NEEDS_REVIEW", "NO_EVIDENCE", "GROUNDING_REJECTED", "LLM_UNAVAILABLE"}:
            return
        query, meeting_id, _, conversation_id, user_message_id, _, assistant_mode, owner_user_id, role = row
        if not self.repository.set_status(query_id, "RUNNING"):
            return
        await asyncio.to_thread(self.repository.set_processing_stage, query_id, "WAITING_FOR_GPU")
        # Keep a deterministic empty result for failure paths.  The finally
        # block persists a terminal diagnostic even when the local LLM cannot
        # start or invoke_json raises; without this initialization the cleanup
        # path itself raised UnboundLocalError and hid the real failure.
        result: dict[str, Any] = {}
        created_at = await asyncio.to_thread(self.repository.created_at, query_id)
        queue_wait_ms = 0.0
        if created_at is not None:
            try:
                created_timestamp = created_at.timestamp()
                queue_wait_ms = max(0.0, (time.time() - created_timestamp) * 1000.0)
            except (AttributeError, TypeError, ValueError, OSError):
                queue_wait_ms = max(0.0, float(payload.get("queue_wait_ms") or 0.0))
        else:
            queue_wait_ms = max(0.0, float(payload.get("queue_wait_ms") or 0.0))
        timings: dict[str, float] = {"queue_wait_ms": round(queue_wait_ms, 3)}
        try:
            # A conversational modifier ("повтори", "короче", "подробнее")
            # is not an evidence query by itself. Use only the latest USER
            # turn as a retrieval key; assistant text remains context for the
            # prompt, never a factual evidence source.
            history = await asyncio.to_thread(self.repository.history, conversation_id, user_message_id)
            state = await asyncio.to_thread(self.repository.conversation_state, conversation_id, owner_user_id)
            previous_plan = AssistantQueryPlan.from_mapping(state)
            query_plan = understand_query(query, previous_plan, self.repository.semantic_provider)
            retrieval_plan = build_retrieval_plan(query_plan, self._retrieval_query_for_follow_up(query, history))
            query_plan_metadata = query_plan.to_dict()
            query_plan_metadata["retrievalPlan"] = retrieval_plan.to_dict()
            self.repository.set_query_plan(query_plan_metadata)
            await asyncio.to_thread(self.repository.save_conversation_state, conversation_id, owner_user_id, meeting_id, query, query_plan.to_dict())
            retrieval_query = retrieval_plan.query
            answer_plan = build_answer_plan(query_plan)
            self.repository.set_answer_plan(answer_plan)
            if assistant_mode == "GENERAL_CHAT":
                self.repository.clear_retrieval_metadata()
                self.repository.set_query_plan(query_plan_metadata)
                self.repository.set_answer_plan(answer_plan)
                context, valid, transcript_kind, context_error = "", {}, "GENERAL", None
                system_prompt = (
                    "Отвечай по-русски как доброжелательный универсальный помощник. "
                    "Это обычный чат, поэтому можно объяснять общие темы и помогать с текстами. "
                    "Не выдавай внутренние данные приложения за факты и верни только JSON с answer, voice_answer, evidence_segment_ids и claims. "
                    "Для обычного чата evidence_segment_ids и claims должны быть пустыми массивами. "
                    "voice_answer сделай коротким: не более трёх предложений для озвучивания."
                )
                user_content = f"Вопрос: {query}\n\nПлан запроса (не является источником фактов): {answer_plan}"
            elif assistant_mode == "LIVE_MEETING":
                retrieval_started = time.perf_counter()
                context, valid, transcript_kind, context_error = await asyncio.to_thread(self.repository.live_context, meeting_id, retrieval_query)
                timings["retrieval_ms"] = round((time.perf_counter() - retrieval_started) * 1000.0, 3)
                if not context:
                    await asyncio.to_thread(self.repository.persist, query_id, {}, valid, assistant_mode, transcript_kind, context_error, meeting_id)
                    return
                await asyncio.to_thread(self.repository.snapshot_evidence, query_id, valid)
                evidence_bundles = build_evidence_bundles(valid, query_plan)
                answer_plan = build_answer_plan(query_plan, detect_conflicts(valid, query_plan), evidence_bundles)
                self.repository.set_answer_plan(answer_plan)
                system_prompt = (
                    "Отвечай по-русски только по свежим provisional ASR-фрагментам текущего совещания. "
                    "Это оперативный черновой контекст, не финальная стенограмма: не добавляй факты, которых нет в сегментах. "
                    "Верни только JSON с answer, voice_answer, evidence_segment_ids и claims. Каждый claim обязан содержать evidenceIds. "
                    "voice_answer сделай коротким: не более трёх предложений для озвучивания."
                )
                user_content = f"Вопрос: {query}\nПлан запроса (не evidence): {answer_plan}\n\nСвежие live-фрагменты (не V1/V2):\n{context}"
            else:
                retrieval_started = time.perf_counter()
                include_all = role in {"Administrator", "Operator", "ADMIN", "OPERATOR"}
                comparison_queries = tuple(
                    f"{topic} {' '.join(query_plan.retrieval_terms)}".strip()
                    for topic in retrieval_plan.split_topics
                )
                memory_result = None
                if assistant_mode == "MEETING_MEMORY" and not comparison_queries:
                    memory_plan = build_memory_query_plan(query_plan.intent, retrieval_query, query_plan.topic)
                    query_plan_metadata["memoryPlan"] = memory_plan.to_dict()
                    self.repository.set_query_plan(query_plan_metadata)
                    memory_result = await asyncio.to_thread(
                        self.repository.memory_context,
                        meeting_id,
                        retrieval_query,
                        owner_user_id,
                        include_all,
                        memory_plan,
                        retrieval_plan.neighbour_window,
                    )
                if memory_result is not None:
                    context, valid, transcript_kind, context_error = memory_result
                else:
                    context_loader = self.repository.context_for_plan if comparison_queries else self.repository.context
                    context_args = (meeting_id, comparison_queries or retrieval_query, owner_user_id, include_all, retrieval_plan.neighbour_window)
                    context, valid, transcript_kind, context_error = await asyncio.to_thread(context_loader, *context_args)
                timings["retrieval_ms"] = round((time.perf_counter() - retrieval_started) * 1000.0, 3)
                if not context:
                    await asyncio.to_thread(self.repository.persist, query_id, {}, valid, assistant_mode, transcript_kind, context_error, meeting_id)
                    return
                # Persist the prompt snapshot before any model invocation.
                # A failed Qwen request remains diagnosable and cannot alter
                # the evidence it actually received.
                await asyncio.to_thread(self.repository.snapshot_evidence, query_id, valid)
                evidence_bundles = build_evidence_bundles(valid, query_plan)
                answer_plan = build_answer_plan(query_plan, detect_conflicts(valid, query_plan), evidence_bundles)
                self.repository.set_answer_plan(answer_plan)
                system_prompt = (
                    "Отвечай по-русски. Используй только приведённые сегменты стенограмм. "
                    "Не выдумывай факты. Верни только JSON с answer, voice_answer, evidence_segment_ids и claims. Каждый claim обязан содержать evidenceIds. "
                    "voice_answer сделай коротким: не более трёх предложений для озвучивания."
                )
                user_content = f"Вопрос: {query}\nПлан запроса (не evidence): {answer_plan}\n\nКонтекст стенограмм:\n{context}"
            messages = [
                {"role": "system", "content": system_prompt},
                *history,
                {"role": "user", "content": user_content},
            ]
            synthesis_completed = False
            async with self.lease:
                preemption_started = time.perf_counter()
                if not await asyncio.to_thread(self._gpu_coordination.llm_may_start):
                    # A healthy ASR lease is a durable wait state, not an
                    # infrastructure failure.  Keep it out of the normal
                    # retry budget so the query remains queued until the GPU
                    # becomes available.
                    raise AssistantGpuBusy("ASSISTANT_WAITING_FOR_GPU")
                timings["preemption_wait_ms"] = round((time.perf_counter() - preemption_started) * 1000.0, 3)
                await asyncio.to_thread(self.repository.set_processing_stage, query_id, "LOADING_MODEL")
                model_started = time.perf_counter()
                server = await asyncio.to_thread(self._llm_runtime.ensure_started)
                timings["model_start_ms"] = round((time.perf_counter() - model_started) * 1000.0, 3)
                if not await asyncio.to_thread(self._gpu_coordination.mark_llm_resident, self._llm_owner, "ASSISTANT", query_id):
                    await asyncio.to_thread(self._llm_runtime.stop)
                    raise AssistantGpuBusy("ASSISTANT_WAITING_FOR_GPU")
                await asyncio.to_thread(self._gpu_coordination.mark_llm_busy, self._llm_owner, "ASSISTANT", query_id)
                try:
                    client = self._client_for(server.base_url)
                    await asyncio.to_thread(self.repository.set_processing_stage, query_id, "GENERATING")
                    generation_started = time.perf_counter()
                    result = await client.invoke_json(
                        messages,
                        ASSISTANT_SCHEMA,
                        max_output_tokens=int(os.getenv("ASSISTANT_MAX_OUTPUT_TOKENS", "384")),
                        retry_max_output_tokens=int(os.getenv("ASSISTANT_GROUNDING_RETRY_MAX_OUTPUT_TOKENS", "512")),
                        retry_instruction="Повтори ответ строго одним валидным JSON; используй только существующие evidenceIds и короткий voice_answer.",
                    )
                    if client.last_first_token_ms is not None:
                        timings["first_token_ms"] = round(client.last_first_token_ms, 3)
                    timings["generation_ms"] = round((time.perf_counter() - generation_started) * 1000.0, 3)
                    await asyncio.to_thread(self.repository.set_processing_stage, query_id, "GROUNDING")
                    retrieval_anchors = int(self._last_retrieval_metadata.get("anchorCount", 0) or 0)
                    if assistant_mode != "GENERAL_CHAT" and retrieval_anchors > 0 and not claims_are_semantically_grounded(result, valid, assistant_mode):
                        # One controlled retry is allowed.  The second result
                        # is still validated by persist(), so a malformed or
                        # unsupported answer can never become READY.
                        retry_messages = [
                            *messages,
                            {"role": "user", "content": "Проверка grounding не пройдена. Верни только claims с существующими evidenceIds из контекста; каждый факт обязан иметь хотя бы один источник."},
                        ]
                        retry_started = time.perf_counter()
                        result = await client.invoke_json(
                            retry_messages,
                            ASSISTANT_SCHEMA,
                            max_output_tokens=int(os.getenv("ASSISTANT_MAX_OUTPUT_TOKENS", "384")),
                            retry_max_output_tokens=int(os.getenv("ASSISTANT_GROUNDING_RETRY_MAX_OUTPUT_TOKENS", "512")),
                            retry_instruction="Исправь grounding: используй только существующие evidenceIds, числа и даты из контекста; voice_answer не более трёх предложений.",
                        )
                        if client.last_first_token_ms is not None:
                            timings["grounding_retry_first_token_ms"] = round(client.last_first_token_ms, 3)
                        timings["generation_ms"] = round(timings.get("generation_ms", 0.0) + (time.perf_counter() - retry_started) * 1000.0, 3)
                    grounding_started = time.perf_counter()
                    if assistant_mode != "GENERAL_CHAT":
                        claims_are_semantically_grounded(result, valid, assistant_mode)
                    timings["grounding_ms"] = round((time.perf_counter() - grounding_started) * 1000.0, 3)
                    synthesis_completed = True
                finally:
                    await asyncio.to_thread(self._llm_runtime.release_after_job)
                    if self._llm_runtime.enabled:
                        if not await asyncio.to_thread(self._gpu_coordination.mark_llm_resident, self._llm_owner):
                            await asyncio.to_thread(self._gpu_coordination.preempt_if_requested, self._llm_runtime, self._llm_owner)
                    else:
                        await asyncio.to_thread(self._gpu_coordination.mark_llm_stopped, self._llm_owner)
                    # Do not turn a failed model start, timeout or malformed
                    # response into a synthetic terminal answer.  The outer
                    # failure path must be able to record FAILED/LLM_UNAVAILABLE
                    # and leave the durable query retryable.
                    if synthesis_completed:
                        timings["total_ms"] = round((time.perf_counter() - total_started) * 1000.0, 3)
                        await asyncio.to_thread(self.repository.persist, query_id, result, valid, assistant_mode, transcript_kind, expected_meeting_id=meeting_id, timings=timings)
        except AssistantGpuBusy as exc:
            state = await asyncio.to_thread(self.repository.schedule_gpu_wait, query_id, message_id or None)
            if state == "QUEUED":
                LOGGER.info("assistant query=%s waiting for GPU; retry_count unchanged", query_id)
                raise AssistantGpuWaitScheduled(float(os.getenv("ASSISTANT_GPU_WAIT_RETRY_SECONDS", "15"))) from exc
            # Expired requests are durably terminal and must be ACKed by the
            # consumer; never turn this bounded wait into NATS redelivery.
            return
        except Exception as exc:
            detail = f"{type(exc).__name__}: {exc}"
            if is_retryable_assistant_error(exc):
                scheduled_attempt = await asyncio.to_thread(
                    self.repository.schedule_retry,
                    query_id,
                    detail,
                    message_id or None,
                    ASSISTANT_MAX_RETRIES,
                )
                if scheduled_attempt is not None:
                    delay = assistant_retry_delay_seconds(scheduled_attempt)
                    LOGGER.warning(
                        "assistant query=%s retry scheduled attempt=%s delay=%ss",
                        query_id,
                        scheduled_attempt,
                        delay,
                    )
                    raise AssistantRetryScheduled(delay, scheduled_attempt) from exc
                # The retry budget is exhausted.  Persist a terminal state and
                # let the consumer ACK the current delivery; a terminal
                # assistant request must not loop forever in JetStream.
                self.repository.set_status(query_id, "LLM_UNAVAILABLE", error="ASSISTANT_RETRY_EXHAUSTED")
                LOGGER.error("assistant query=%s retry budget exhausted: %s", query_id, detail)
                return
            # Deterministic validation/scope/model configuration failures are
            # terminal.  They remain visible to Desktop and cannot be retried
            # by a stale NATS delivery.
            self.repository.set_status(query_id, "FAILED", error=type(exc).__name__.upper())
            LOGGER.error("assistant query=%s entered terminal failure: %s", query_id, detail)
            return
