from __future__ import annotations

import asyncio
import logging
import os
import re
import socket
from typing import Any

import psycopg
from psycopg.types.json import Jsonb

from workers.gpu_lease import PostgresGpuLease
from .llama_subprocess import LocalLlamaServer
from .summarizer import LlamaCppClient

LOGGER = logging.getLogger("whisperx.assistant-worker")

ASSISTANT_SCHEMA: dict[str, Any] = {
    "type": "object",
    "required": ["answer", "voice_answer", "evidence_segment_ids"],
    "properties": {
        "answer": {"type": "string", "maxLength": 4000},
        "voice_answer": {"type": "string", "maxLength": 500},
        "evidence_segment_ids": {"type": "array", "maxItems": 8, "items": {"type": "string"}},
    },
}


class AssistantRepository:
    def __init__(self) -> None:
        self.conninfo = os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")

    def claim(self, message_id: str, query_id: str) -> bool:
        with psycopg.connect(self.conninfo) as connection:
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
        with psycopg.connect(self.conninfo) as connection:
            connection.execute(
                "UPDATE assistant_queries SET updated_at=now() WHERE id=%s AND status NOT IN ('READY','FAILED','NEEDS_REVIEW')",
                (query_id,),
            )
            if message_id:
                connection.execute(
                    "UPDATE inbox_messages SET lease_expires_at=now()+interval '30 minutes',worker_id=%s WHERE message_id=%s",
                    (socket.gethostname(), message_id),
                )

    def query(self, query_id: str) -> tuple[str, str | None, str, str | None, str | None, str | None, str] | None:
        with psycopg.connect(self.conninfo) as connection:
            row = connection.execute("SELECT query,meeting_id,status,conversation_id,user_message_id,assistant_message_id,assistant_mode FROM assistant_queries WHERE id=%s", (query_id,)).fetchone()
            return (str(row[0]), str(row[1]) if row[1] else None, str(row[2]), str(row[3]) if row[3] else None, str(row[4]) if row[4] else None, str(row[5]) if row[5] else None, str(row[6] or "MEETING_MEMORY")) if row else None

    def set_status(self, query_id: str, status: str, *, error: str | None = None) -> bool:
        with psycopg.connect(self.conninfo) as connection:
            row = connection.execute("""
                UPDATE assistant_queries SET status=%s,error_code=%s
                WHERE id=%s AND status NOT IN ('READY','FAILED','NEEDS_REVIEW')
                RETURNING id,assistant_message_id
                """, (status, error, query_id)).fetchone()
            if row and row[1]:
                connection.execute("UPDATE assistant_messages SET status=%s,error_code=%s WHERE id=%s", (status, error, row[1]))
            return row is not None

    def context(self, meeting_id: str | None, query: str = "") -> tuple[str, dict[str, tuple[str, int, int]]]:
        with psycopg.connect(self.conninfo) as connection:
            if meeting_id:
                rows = connection.execute(
                    """
                    SELECT s.id,t.meeting_id,s.start_ms,s.end_ms,COALESCE(ms.display_name,s.speaker_label,'Спикер N'),s.text
                    FROM transcript_segments s JOIN transcripts t ON t.id=s.transcript_id
                    LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id
                    JOIN meetings m ON m.id=t.meeting_id
                    WHERE t.meeting_id=%s AND t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id) AND t.status IN ('READY','PARTIAL_READY') AND COALESCE(s.is_hidden,false)=false
                    ORDER BY s.ordinal LIMIT 1200
                    """,
                    (meeting_id,),
                ).fetchall()
            else:
                rows = connection.execute(
                    """
                    SELECT s.id,t.meeting_id,s.start_ms,s.end_ms,COALESCE(ms.display_name,s.speaker_label,'Спикер N'),s.text
                    FROM transcript_segments s JOIN transcripts t ON t.id=s.transcript_id
                    LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id
                    JOIN meetings m ON m.id=t.meeting_id
                    WHERE t.version=(SELECT MAX(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id) AND t.status IN ('READY','PARTIAL_READY') AND m.created_at >= now()-interval '90 days' AND COALESCE(s.is_hidden,false)=false
                    ORDER BY m.created_at DESC,s.ordinal LIMIT 2400
                    """
                ).fetchall()
        max_chars = max(4000, int(os.getenv("ASSISTANT_MAX_CONTEXT_CHARS", "36000")))
        if rows:
            full_text = "\n".join(f"[SEG-{row[0]} {int(row[2]) // 1000}s {row[4]}] {str(row[5]).strip()}" for row in rows)
            if len(full_text) > max_chars:
                tokens = {token.lower() for token in re.findall(r"[\wА-Яа-яЁё]{3,}", query)}
                scored = []
                for index, row in enumerate(rows):
                    text_tokens = {token.lower() for token in re.findall(r"[\wА-Яа-яЁё]{3,}", str(row[5]))}
                    scored.append((len(tokens & text_tokens), index, row))
                selected_indexes: set[int] = set()
                for score, index, _ in sorted(scored, key=lambda item: (item[0], item[1]), reverse=True)[:80]:
                    if score == 0 and selected_indexes:
                        break
                    selected_indexes.update(range(max(0, index - 1), min(len(rows), index + 2)))
                rows = [row for index, row in enumerate(rows) if index in selected_indexes]

        valid: dict[str, tuple[str, int, int]] = {}
        lines: list[str] = []
        for segment_id, meeting_id_value, start_ms, end_ms, speaker, text in rows:
            key = str(segment_id)
            valid[key] = (str(meeting_id_value), int(start_ms), int(end_ms))
            lines.append(f"[SEG-{key} {int(start_ms)//1000}s {speaker}] {str(text).strip()}")
        return "\n".join(lines), valid

    def history(self, conversation_id: str | None, current_user_message_id: str | None) -> list[dict[str, str]]:
        if not conversation_id:
            return []
        max_messages = max(2, int(os.getenv("ASSISTANT_MAX_HISTORY_MESSAGES", "12")))
        max_chars = max(1000, int(os.getenv("ASSISTANT_MAX_HISTORY_CHARS", "12000")))
        with psycopg.connect(self.conninfo) as connection:
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

    def persist(self, query_id: str, result: dict[str, Any], valid: dict[str, tuple[str, int, int]], assistant_mode: str) -> None:
        evidence_ids = [str(value).removeprefix("SEG-") for value in result.get("evidence_segment_ids", [])]
        evidence_ids = list(dict.fromkeys(value for value in evidence_ids if value in valid))[:8]
        answer = str(result.get("answer", "")).strip()
        voice = str(result.get("voice_answer", answer)).strip()
        voice = re.split(r"(?<=[.!?])\s+", voice)
        voice = " ".join(voice[:3])[:500].strip()
        status = "READY" if assistant_mode == "GENERAL_CHAT" or evidence_ids else "NEEDS_REVIEW"
        evidence = [{"meetingId": valid[value][0], "segmentId": value, "startMs": valid[value][1], "endMs": valid[value][2]} for value in evidence_ids]
        with psycopg.connect(self.conninfo) as connection:
            row = connection.execute(
                "UPDATE assistant_queries SET status=%s,answer=%s,voice_answer=%s,evidence=%s::jsonb,error_code=NULL,completed_at=now() WHERE id=%s AND status NOT IN ('READY','FAILED','NEEDS_REVIEW') RETURNING assistant_message_id,conversation_id",
                (status, answer, voice, Jsonb(evidence), query_id),
            ).fetchone()
            if row and row[0]:
                connection.execute(
                    "UPDATE assistant_messages SET status=%s,content=%s,voice_answer=%s,evidence=%s::jsonb,error_code=NULL,completed_at=now() WHERE id=%s",
                    (status, answer, voice, Jsonb(evidence), row[0]),
                )
            if row and row[1]:
                connection.execute("UPDATE assistant_conversations SET updated_at=now() WHERE id=%s", (row[1],))


class AssistantWorker:
    def __init__(self) -> None:
        self.repository = AssistantRepository()
        self.lease = PostgresGpuLease(self.repository.conninfo)
        self.model_alias = os.getenv("LLM_MODEL_ALIAS", "qwen3-8b")

    async def handle(self, payload: dict[str, Any]) -> None:
        query_id = str(payload["query_id"])
        message_id = str(payload.get("message_id", ""))
        if message_id and not self.repository.claim(message_id, query_id):
            return
        row = self.repository.query(query_id)
        if row is None or row[2] in {"READY", "FAILED", "NEEDS_REVIEW"}:
            return
        query, meeting_id, _, conversation_id, user_message_id, _, assistant_mode = row
        if not self.repository.set_status(query_id, "RUNNING"):
            return
        try:
            if assistant_mode == "GENERAL_CHAT":
                context, valid = "", {}
                system_prompt = (
                    "Отвечай по-русски как доброжелательный универсальный помощник. "
                    "Это обычный чат, поэтому можно объяснять общие темы и помогать с текстами. "
                    "Не выдавай внутренние данные приложения за факты и верни только JSON с answer, voice_answer и evidence_segment_ids. "
                    "Для обычного чата evidence_segment_ids должен быть пустым массивом."
                )
                user_content = f"Вопрос: {query}"
            else:
                context, valid = await asyncio.to_thread(self.repository.context, meeting_id, query)
                if not context:
                    raise RuntimeError("assistant_context_empty")
                system_prompt = (
                    "Отвечай по-русски. Используй только приведённые сегменты стенограмм. "
                    "Не выдумывай факты. Верни только JSON с answer, voice_answer и evidence_segment_ids."
                )
                user_content = f"Вопрос: {query}\n\nКонтекст стенограмм:\n{context}"
            history = await asyncio.to_thread(self.repository.history, conversation_id, user_message_id)
            messages = [
                {"role": "system", "content": system_prompt},
                *history,
                {"role": "user", "content": user_content},
            ]
            async with self.lease:
                server = LocalLlamaServer()
                await asyncio.to_thread(server.start)
                try:
                    result = await LlamaCppClient(server.base_url, self.model_alias).invoke_json(messages, ASSISTANT_SCHEMA)
                finally:
                    await asyncio.to_thread(server.stop)
            await asyncio.to_thread(self.repository.persist, query_id, result, valid, assistant_mode)
        except Exception as exc:
            self.repository.set_status(query_id, "FAILED", error=type(exc).__name__.upper())
            LOGGER.exception("assistant query failed: %s", query_id)
            raise
