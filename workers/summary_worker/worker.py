from __future__ import annotations

import asyncio
import json
import os
import socket
from datetime import datetime
from typing import Any
import logging

import psycopg
from psycopg.types.json import Jsonb

from workers.gpu_lease import PostgresGpuLease
from workers.nats_utils import fetch_available
from .summarizer import LlamaCppClient, SummaryOrchestrator, TranscriptSegment
from .llama_subprocess import LocalLlamaServer

LOGGER = logging.getLogger("whisperx.summary-worker")


class SummaryRepository:
    def __init__(self) -> None:
        self.conninfo = os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")

    def claim(self, message_id: str, job_id: str) -> bool:
        with psycopg.connect(self.conninfo) as connection:
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
        with psycopg.connect(self.conninfo) as connection:
            row = connection.execute("SELECT status FROM jobs WHERE id=%s", (job_id,)).fetchone()
            return str(row[0]) if row else None

    def update_job(self, job_id: str, status: str, stage: str, progress: int, error: str | None = None) -> None:
        with psycopg.connect(self.conninfo) as connection:
            connection.execute(
                "UPDATE jobs SET status=%s,stage=%s,progress=%s,error_message=%s,worker_id=%s,lease_expires_at=now()+interval '30 minutes',last_heartbeat=now(),updated_at=now() WHERE id=%s",
                (status, stage, progress, error, socket.gethostname(), job_id),
            )

    def mark_failed(self, job_id: str, meeting_id: str, error: str) -> None:
        with psycopg.connect(self.conninfo) as connection:
            connection.execute(
                "UPDATE jobs SET status='FAILED',stage='FAILED',progress=0,error_message=%s,error_code='SUMMARY_FAILED',lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now() WHERE id=%s",
                (error, job_id),
            )
            # The transcript remains usable even when the optional summary failed.
            connection.execute("UPDATE meetings SET status='PARTIAL_READY' WHERE id=%s AND status <> 'READY'", (meeting_id,))
    def load_segments(self, meeting_id: str, transcript_id: str | None = None) -> list[TranscriptSegment]:
        with psycopg.connect(self.conninfo) as connection:
            if transcript_id:
                rows = connection.execute(
                    """
                    SELECT s.id,s.start_ms,s.end_ms,COALESCE(ms.display_name,s.speaker_label,'Спикер N'),s.text
                    FROM transcript_segments s
                    JOIN transcripts t ON t.id=s.transcript_id
                    LEFT JOIN meeting_speakers ms ON ms.id=s.speaker_id
                    WHERE t.id=%s AND t.meeting_id=%s
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
                    WHERE t.meeting_id=%s AND t.version=(SELECT MAX(version) FROM transcripts WHERE meeting_id=%s)
                    ORDER BY s.ordinal
                    """,
                    (meeting_id, meeting_id),
                ).fetchall()
        return [TranscriptSegment(str(row[0]), int(row[1]), int(row[2]), str(row[3]), str(row[4])) for row in rows]

    def persist(self, job_id: str, meeting_id: str, transcript_id: str | None, result: dict[str, Any], model_name: str) -> None:
        source_hash = str(result["source_hash"])
        with psycopg.connect(self.conninfo) as connection:
            # Serialize summary versions and decision/task inserts for this meeting.
            if connection.execute("SELECT id FROM meetings WHERE id=%s FOR UPDATE", (meeting_id,)).fetchone() is None:
                raise RuntimeError("meeting_not_found")
            if transcript_id:
                transcript = connection.execute("SELECT id FROM transcripts WHERE id=%s AND meeting_id=%s", (transcript_id, meeting_id)).fetchone()
            else:
                transcript = connection.execute("SELECT id FROM transcripts WHERE meeting_id=%s ORDER BY version DESC LIMIT 1", (meeting_id,)).fetchone()
            if transcript is None:
                raise RuntimeError("transcript_not_found")
            current = connection.execute("SELECT COALESCE(MAX(version),0) FROM summaries WHERE meeting_id=%s", (meeting_id,)).fetchone()[0]
            summary_id = connection.execute(
                "INSERT INTO summaries(id,meeting_id,transcript_id,version,status,model_name,prompt_version,source_hash,content) VALUES(gen_random_uuid(),%s,%s,%s,'DRAFT',%s,'summary-v1',%s,%s::jsonb) RETURNING id",
                (meeting_id, transcript[0], int(current) + 1, model_name, source_hash, Jsonb(result)),
            ).fetchone()[0]
            valid_segments = {str(row[0]): (int(row[1]), int(row[2])) for row in connection.execute("SELECT id,start_ms,end_ms FROM transcript_segments WHERE transcript_id=%s", (transcript[0],)).fetchall()}
            for collection in ("decisions", "risks", "open_questions"):
                for index, item in enumerate(result.get(collection, [])):
                    evidence = [str(value) for value in item.get("evidence_segment_ids", []) if str(value) in valid_segments]
                    for segment_id in evidence:
                        start_ms, end_ms = valid_segments[segment_id]
                        connection.execute("INSERT INTO summary_evidence(id,summary_id,entity_type,entity_key,segment_id,start_ms,end_ms) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s)", (summary_id, collection, str(index), segment_id, start_ms, end_ms))
                    if collection == "decisions" and item.get("text"):
                        connection.execute("INSERT INTO decisions(id,meeting_id,summary_id,text,status) VALUES(gen_random_uuid(),%s,%s,%s,'DRAFT')", (meeting_id, summary_id, str(item["text"])))
            for index, item in enumerate(result.get("action_items", [])):
                task_text = str(item.get("task", "")).strip()
                if not task_text:
                    continue
                evidence = [str(value) for value in item.get("evidence_segment_ids", []) if str(value) in valid_segments]
                evidence_id = evidence[0] if evidence else None
                connection.execute(
                    "INSERT INTO action_items(id,meeting_id,summary_id,task,responsible,deadline,status,evidence_segment_id) VALUES(gen_random_uuid(),%s,%s,%s,%s,%s,%s,%s)",
                    (meeting_id, summary_id, task_text, item.get("responsible"), parse_deadline(item.get("deadline")), "NEEDS_REVIEW", evidence_id),
                )
            connection.execute("UPDATE summaries SET status='READY' WHERE id=%s", (summary_id,))
            connection.execute("UPDATE jobs SET status='READY',stage='READY',progress=100,error_message=NULL,error_code=NULL,lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now() WHERE id=%s", (job_id,))
            connection.execute("UPDATE meetings SET status='READY' WHERE id=%s", (meeting_id,))

def parse_deadline(value: Any) -> datetime | None:
    if not value or not isinstance(value, str):
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


class SummaryWorker:
    def __init__(self) -> None:
        self.repository = SummaryRepository()
        self._gpu_lease = PostgresGpuLease(self.repository.conninfo)
        self.model_alias = os.getenv("LLM_MODEL_ALIAS", "qwen3-8b")


    async def handle(self, payload: dict[str, Any]) -> None:
        job_id = str(payload["job_id"])
        meeting_id = str(payload["meeting_id"])
        transcript_id = str(payload["transcript_id"]) if payload.get("transcript_id") else None
        message_id = str(payload.get("message_id", ""))
        if message_id and not self.repository.claim(message_id, job_id):
            return
        current = self.repository.job_state(job_id)
        if current in {"READY", "FAILED", "CANCELLED"}:
            LOGGER.info("skip terminal summary job=%s status=%s", job_id, current)
            return
        self.repository.update_job(job_id, "RUNNING", "SUMMARIZING", 20)
        try:
            segments = await asyncio.to_thread(self.repository.load_segments, meeting_id, transcript_id)
            LOGGER.info("job=%s waiting for GPU lease", job_id)
            async with self._gpu_lease:
                LOGGER.info("job=%s acquired GPU lease", job_id)
                server = LocalLlamaServer()
                await asyncio.to_thread(server.start)
                try:
                    client = LlamaCppClient(server.base_url, self.model_alias)
                    result = await SummaryOrchestrator(client.invoke_json).summarize(segments)
                finally:
                    await asyncio.to_thread(server.stop)
            LOGGER.info("job=%s released GPU lease", job_id)
            await asyncio.to_thread(self.repository.persist, job_id, meeting_id, transcript_id, result, self.model_alias)
        except Exception as exc:
            self.repository.mark_failed(job_id, meeting_id, type(exc).__name__ + ": " + str(exc))
            raise


async def run() -> None:
    try:
        import nats
    except ImportError as exc:
        raise RuntimeError("Install workers/summary_worker/requirements.txt") from exc
    client = await nats.connect(os.getenv("NATS_URL", "nats://nats:4222"))
    jetstream = client.jetstream()
    try:
        await jetstream.add_stream(name="WHISPERX", subjects=["media.ingest", "ml.transcribe", "llm.summarize"])
    except Exception:
        pass
    subscription = await jetstream.pull_subscribe("llm.summarize", durable="summary-worker")
    worker = SummaryWorker()
    while True:
        for message in await fetch_available(subscription, nats.errors.TimeoutError):
            try:
                await worker.handle(json.loads(message.data))
                await message.ack()
            except Exception:
                await message.nak()


if __name__ == "__main__":
    asyncio.run(run())
