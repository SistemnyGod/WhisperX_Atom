from __future__ import annotations

import asyncio
import logging
import os
import re
import socket
from typing import Any

from psycopg.types.json import Jsonb

from workers.db_pool import DatabaseConnectionPool
from workers.gpu_lease import PostgresGpuLease
from .llama_subprocess import LocalLlamaRuntime
from .summarizer import LlamaCppClient

LOGGER = logging.getLogger("whisperx.assistant-worker")
# Kept as a compatibility marker for older Desktop/Voice clients. New
# clients receive the explicit NO_EVIDENCE error code below, while rolling
# upgrades may still look for the historical empty-context name.
LEGACY_EMPTY_CONTEXT_ERROR = "assistant_context_empty"
# The original terminal set remains part of the compatibility contract:
# status NOT IN ('READY','FAILED','NEEDS_REVIEW'). New explicit terminal
# states are appended below rather than changing the meaning of old clients.

ASSISTANT_SCHEMA: dict[str, Any] = {
    "type": "object",
    "required": ["answer", "voice_answer", "evidence_segment_ids"],
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


def claims_are_structurally_grounded(result: dict[str, Any], valid: dict[str, tuple[str, int, int, str, str]], assistant_mode: str) -> bool:
    """Fail closed on evidence IDs without trying to judge prose semantics."""
    claims = result.get("claims")
    if assistant_mode == "GENERAL_CHAT":
        return not claims or all(not isinstance(item, dict) or not (item.get("evidenceIds") or []) for item in claims)
    if claims is None:
        # Old Qwen contract remains accepted during rolling upgrades.
        return True
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


def claims_are_semantically_grounded(result: dict[str, Any], valid: dict[str, tuple[str, int, int, str, str]], assistant_mode: str) -> bool:
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
        claim_tokens = {
            token for token in re.findall(r"[\wА-Яа-яЁё-]{2,}", claim_text)
            if token not in _GROUNDING_STOPWORDS and not token.startswith("seg-")
        }
        evidence_tokens = set(re.findall(r"[\wА-Яа-яЁё-]{2,}", evidence_text))
        if claim_tokens and not (claim_tokens & evidence_tokens):
            return False
        numeric_tokens = set(re.findall(r"\d+(?:[.,]\d+)?", claim_text))
        if any(token not in evidence_text for token in numeric_tokens):
            return False
    return True


class AssistantRepository:
    def __init__(self) -> None:
        self.conninfo = os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")
        self._db = DatabaseConnectionPool(self.conninfo, "assistant-worker")

    def close(self) -> None:
        self._db.close()

    def claim(self, message_id: str, query_id: str) -> bool:
        with self._db.connection() as connection:
            row = connection.execute(
                """
                INSERT INTO inbox_messages(message_id,job_id,lease_expires_at,worker_id)
                VALUES(%s,%s,now()+interval '30 minutes',%s)
                ON CONFLICT(message_id) DO UPDATE SET lease_expires_at=excluded.lease_expires_at,worker_id=excluded.worker_id
                WHERE inbox_messages.lease_expires_at IS NULL OR inbox_messages.lease_expires_at < now()
                RETURNING message_id
                """,
                (message_id, query_id, socket.gethostname()),
            ).fetchone()
            return row is not None

    def renew_lease(self, query_id: str, message_id: str | None = None) -> None:
        with self._db.connection() as connection:
            connection.execute(
                "UPDATE assistant_queries SET updated_at=now() WHERE id=%s AND status NOT IN ('READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED','NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED','LLM_UNAVAILABLE')",
                (query_id,),
            )
            if message_id:
                connection.execute(
                    "UPDATE inbox_messages SET lease_expires_at=now()+interval '30 minutes',worker_id=%s WHERE message_id=%s",
                    (socket.gethostname(), message_id),
                )

    def query(self, query_id: str) -> tuple[str, str | None, str, str | None, str | None, str | None, str, str | None, str | None] | None:
        with self._db.connection() as connection:
            row = connection.execute("SELECT q.query,q.meeting_id,q.status,q.conversation_id,q.user_message_id,q.assistant_message_id,q.assistant_mode,q.user_id,u.role FROM assistant_queries q LEFT JOIN users u ON u.id=q.user_id WHERE q.id=%s", (query_id,)).fetchone()
            return (str(row[0]), str(row[1]) if row[1] else None, str(row[2]), str(row[3]) if row[3] else None, str(row[4]) if row[4] else None, str(row[5]) if row[5] else None, str(row[6] or "MEETING_MEMORY"), str(row[7]) if row[7] else None, str(row[8]) if row[8] else None) if row else None

    def set_status(self, query_id: str, status: str, *, error: str | None = None) -> bool:
        with self._db.connection() as connection:
            row = connection.execute("""
                UPDATE assistant_queries SET status=%s,error_code=%s
                WHERE id=%s AND status NOT IN ('READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED','NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED','LLM_UNAVAILABLE')
                RETURNING id,assistant_message_id
                """, (status, error, query_id)).fetchone()
            if row and row[1]:
                connection.execute("UPDATE assistant_messages SET status=%s,error_code=%s WHERE id=%s", (status, error, row[1]))
            return row is not None

    def context(self, meeting_id: str | None, query: str = "", owner_user_id: str | None = None, include_all: bool = False) -> tuple[str, dict[str, tuple[str, int, int, str, str]], str, str | None]:
        # Retrieval is performed by PostgreSQL's Russian FTS instead of
        # loading an entire meeting and scoring it in Python. The CTE keeps
        # the twelve strongest hits and adds one neighbouring segment on each
        # side, while the outer limit protects the model prompt.
        with self._db.connection() as connection:
            rows = connection.execute(
                """
                WITH source AS (
                    SELECT s.id,t.meeting_id,s.start_ms,s.end_ms,
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
                SELECT id,meeting_id,start_ms,end_ms,speaker,text,version_kind
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
        max_chars = min(36000, max(4000, int(os.getenv("ASSISTANT_MAX_CONTEXT_CHARS", "36000"))))
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

        valid: dict[str, tuple[str, int, int, str, str]] = {}
        lines: list[str] = []
        kinds: set[str] = set()
        for segment_id, meeting_id_value, start_ms, end_ms, speaker, text, version_kind in rows:
            key = str(segment_id)
            kind = str(version_kind or "ASR_DRAFT").upper()
            kinds.add(kind)
            valid[key] = (str(meeting_id_value), int(start_ms), int(end_ms), str(text).strip(), kind)
            lines.append(f"[SEG-{key} {int(start_ms)//1000}s {speaker}] {str(text).strip()}")
        context = "\n".join(lines)
        if len(context) > max_chars:
            context = context[:max_chars].rsplit("\n", 1)[0]
            allowed = {line.split(" ", 1)[0].removeprefix("[SEG-") for line in context.splitlines()}
            valid = {key: value for key, value in valid.items() if key in allowed}
        return context, valid, ("ASR_DRAFT" if kinds and kinds <= {"ASR_DRAFT", "V1"} else "ENRICHED"), ("LOW_TRANSCRIPT_QUALITY" if low_quality else None)

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

    def persist(self, query_id: str, result: dict[str, Any], valid: dict[str, tuple[str, int, int, str, str]], assistant_mode: str, transcript_kind: str = "ENRICHED", reason: str | None = None) -> None:
        claims = result.get("claims") if isinstance(result.get("claims"), list) else []
        claim_ids = [str(item).removeprefix("SEG-") for claim in claims if isinstance(claim, dict) for item in (claim.get("evidenceIds") or [])]
        evidence_ids = claim_ids or [str(value).removeprefix("SEG-") for value in result.get("evidence_segment_ids", [])]
        evidence_ids = list(dict.fromkeys(value for value in evidence_ids if value in valid))[:8]
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
        elif reason == "LOW_TRANSCRIPT_QUALITY":
            status = "NEEDS_REVIEW"
            grounding_status = "WARNING"
            error_code = "LOW_TRANSCRIPT_QUALITY"
            answer = "Эта стенограмма требует проверки качества перед ответом."
            voice = "Сначала проверьте качество стенограммы."
        elif evidence_ids and answer and claims_are_semantically_grounded(result, valid, assistant_mode):
            status = "ANSWERED_WITH_WARNING" if transcript_kind == "ASR_DRAFT" else "READY"
            grounding_status = "WARNING" if transcript_kind == "ASR_DRAFT" else "GROUNDED"
            error_code = None
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
        evidence = [{"meetingId": valid[value][0], "segmentId": value, "startMs": valid[value][1], "endMs": valid[value][2], "transcriptVersionKind": valid[value][4]} for value in evidence_ids]
        with self._db.connection() as connection:
            row = connection.execute(
                "UPDATE assistant_queries SET status=%s,answer=%s,voice_answer=%s,evidence=%s::jsonb,error_code=%s,grounding_status=%s,answer_metadata=%s::jsonb,completed_at=now() WHERE id=%s AND status NOT IN ('READY','ANSWERED','ANSWERED_WITH_WARNING','FAILED','NEEDS_REVIEW','NO_EVIDENCE','GROUNDING_REJECTED','LLM_UNAVAILABLE') RETURNING assistant_message_id,conversation_id",
                (status, answer, voice, Jsonb(evidence), error_code, grounding_status, Jsonb({"evidenceCount": len(evidence_ids), "assistantMode": assistant_mode, "transcriptKind": transcript_kind, "claimsValidated": bool(claims), "legacyErrorCode": LEGACY_EMPTY_CONTEXT_ERROR if error_code == "NO_EVIDENCE" else None}), query_id),
            ).fetchone()
            if evidence_ids:
                connection.execute("DELETE FROM assistant_query_evidence WHERE query_id=%s", (query_id,))
                for rank, segment_id in enumerate(evidence_ids, start=1):
                    meeting_value, start_ms, end_ms, _, _ = valid[segment_id]
                    connection.execute(
                        "INSERT INTO assistant_query_evidence(query_id,segment_id,rank,transcript_id,transcript_version) SELECT %s,%s,%s,t.id,t.version FROM transcripts t WHERE t.meeting_id=%s AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id) LIMIT 1 ON CONFLICT DO NOTHING",
                        (query_id, segment_id, rank, meeting_value),
                    )
            if row and row[0]:
                connection.execute(
                    "UPDATE assistant_messages SET status=%s,content=%s,voice_answer=%s,evidence=%s::jsonb,error_code=%s,completed_at=now() WHERE id=%s",
                    (status, answer, voice, Jsonb(evidence), error_code, row[0]),
                )
            if row and row[1]:
                connection.execute("UPDATE assistant_conversations SET updated_at=now() WHERE id=%s", (row[1],))


class AssistantWorker:
    def __init__(self) -> None:
        self.repository = AssistantRepository()
        self.lease = PostgresGpuLease(self.repository.conninfo, priority=50)
        self.model_alias = os.getenv("LLM_MODEL_ALIAS", "qwen3-8b")
        self._llm_runtime = LocalLlamaRuntime()
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
        try:
            if assistant_mode == "GENERAL_CHAT":
                context, valid, transcript_kind, context_error = "", {}, "GENERAL", None
                system_prompt = (
                    "Отвечай по-русски как доброжелательный универсальный помощник. "
                    "Это обычный чат, поэтому можно объяснять общие темы и помогать с текстами. "
                    "Не выдавай внутренние данные приложения за факты и верни только JSON с answer, voice_answer, evidence_segment_ids и claims. "
                    "Для обычного чата evidence_segment_ids и claims должны быть пустыми массивами."
                )
                user_content = f"Вопрос: {query}"
            else:
                include_all = role in {"Administrator", "Operator", "ADMIN", "OPERATOR"}
                context, valid, transcript_kind, context_error = await asyncio.to_thread(self.repository.context, meeting_id, query, owner_user_id, include_all)
                if not context:
                    await asyncio.to_thread(self.repository.persist, query_id, {}, valid, assistant_mode, transcript_kind, context_error)
                    return
                system_prompt = (
                    "Отвечай по-русски. Используй только приведённые сегменты стенограмм. "
                    "Не выдумывай факты. Верни только JSON с answer, voice_answer, evidence_segment_ids и claims. Каждый claim обязан содержать evidenceIds."
                )
                user_content = f"Вопрос: {query}\n\nКонтекст стенограмм:\n{context}"
            history = await asyncio.to_thread(self.repository.history, conversation_id, user_message_id)
            messages = [
                {"role": "system", "content": system_prompt},
                *history,
                {"role": "user", "content": user_content},
            ]
            async with self.lease:
                server = await asyncio.to_thread(self._llm_runtime.ensure_started)
                try:
                    client = self._client_for(server.base_url)
                    result = await client.invoke_json(messages, ASSISTANT_SCHEMA)
                    if assistant_mode != "GENERAL_CHAT" and not claims_are_semantically_grounded(result, valid, assistant_mode):
                        # One controlled retry is allowed.  The second result
                        # is still validated by persist(), so a malformed or
                        # unsupported answer can never become READY.
                        retry_messages = [
                            *messages,
                            {"role": "user", "content": "Проверка grounding не пройдена. Верни только claims с существующими evidenceIds из контекста; каждый факт обязан иметь хотя бы один источник."},
                        ]
                        result = await client.invoke_json(retry_messages, ASSISTANT_SCHEMA)
                finally:
                    await asyncio.to_thread(self._llm_runtime.release_after_job)
            await asyncio.to_thread(self.repository.persist, query_id, result, valid, assistant_mode, transcript_kind)
        except Exception as exc:
            self.repository.set_status(query_id, "FAILED", error=type(exc).__name__.upper())
            LOGGER.exception("assistant query failed: %s", query_id)
            raise
