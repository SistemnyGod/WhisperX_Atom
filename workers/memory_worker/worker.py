"""Memory indexing stages and retry-safe job contract.

The durable NATS/PostgreSQL adapter can invoke ``MemoryIndexWorker.index``;
the indexer itself never gates transcript V1/V2 or Summary completion.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import socket
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Iterable, Mapping

try:
    import psycopg
except ImportError:  # pragma: no cover - worker image installs psycopg
    psycopg = None  # type: ignore[assignment]

from .indexer import MemoryIndex, build_memory_index
from .models import MemoryFact
from .entity_resolver import canonical_topic_name, normalize_entity_name
from workers.nats_utils import ensure_stream, fetch_available, maintain_message
from workers.runtime_heartbeat import AsyncHeartbeat

LOGGER = logging.getLogger("whisperx.memory-worker")

MEMORY_MAX_SUBJECTS_PER_JOB = max(1, int(os.getenv("MEMORY_MAX_SUBJECTS_PER_JOB", "64")))
MEMORY_MAX_FACTS_PER_SUBJECT = max(1, int(os.getenv("MEMORY_MAX_FACTS_PER_SUBJECT", "512")))
MEMORY_MAX_FACTS_PER_JOB = max(1, int(os.getenv("MEMORY_MAX_FACTS_PER_JOB", "4096")))


MEMORY_STAGES = (
    "QUEUED",
    "EXTRACTING_FACTS",
    "RESOLVING_ENTITIES",
    "LINKING_FACTS",
    "REBUILDING_THREADS",
    "READY",
    "NEEDS_REVIEW",
    "FAILED",
)


@dataclass(frozen=True)
class MemoryIndexResult:
    status: str
    stage: str
    index: MemoryIndex | None
    error_code: str | None = None


class MemoryIndexWorker:
    """Build an owner-scoped memory projection with fail-closed evidence."""

    def index(self, facts: Iterable[MemoryFact], meeting_order: Mapping[str, int] | None = None) -> MemoryIndexResult:
        try:
            values = tuple(facts)
            if any(fact.state == "ACTIVE" and not fact.evidence_segment_ids for fact in values):
                safe_values = tuple(fact for fact in values if fact.state != "ACTIVE" or fact.evidence_segment_ids)
                return MemoryIndexResult(
                    "NEEDS_REVIEW",
                    "NEEDS_REVIEW",
                    build_memory_index(safe_values, meeting_order=meeting_order),
                    "MEMORY_FACT_WITHOUT_EVIDENCE",
                )
            index = build_memory_index(values, meeting_order=meeting_order)
            if not index.evidence_safe:
                return MemoryIndexResult("NEEDS_REVIEW", "NEEDS_REVIEW", index, "MEMORY_FACT_WITHOUT_EVIDENCE")
            return MemoryIndexResult("READY", "READY", index)
        except Exception as exc:
            return MemoryIndexResult("FAILED", "FAILED", None, type(exc).__name__.upper())


class MemoryProjectionRepository:
    """PostgreSQL adapter for the derived owner-scoped memory projection."""

    def __init__(self, conninfo: str | None = None) -> None:
        self.conninfo = conninfo or os.getenv(
            "DATABASE_URL",
            "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx",
        )
        self.worker_id = os.getenv("WORKER_INSTANCE_ID") or f"memory-worker-{socket.gethostname()}-{os.getpid()}"

    def _connection(self) -> psycopg.Connection:
        if psycopg is None:
            raise RuntimeError("memory_worker_psycopg_missing")
        return psycopg.connect(self.conninfo)

    def claim(self, job_id: str) -> str:
        """Claim a durable job and its inbox lease without stealing a live owner."""
        with self._connection() as connection:
            with connection.transaction():
                job = connection.execute(
                    "SELECT status,attempt,max_attempts,next_retry_at,worker_id,lease_expires_at,owner_user_id,meeting_id,transcript_id,transcript_version FROM memory_jobs WHERE id=%s FOR UPDATE",
                    (job_id,),
                ).fetchone()
                if job is None:
                    return "TERMINAL"
                status, attempt, max_attempts, next_retry, owner, lease, _owner_user_id, _meeting_id, _transcript_id, _transcript_version = job
                status = str(status).upper()
                if status in {"READY", "NEEDS_REVIEW"}:
                    return "TERMINAL"
                now = datetime.now(timezone.utc)
                if next_retry is not None and next_retry > now:
                    return "WAITING"
                if lease is not None and lease > now and owner and str(owner) != self.worker_id:
                    return "OWNED_BY_OTHER_WORKER"
                if status == "FAILED" and int(attempt or 0) >= int(max_attempts or 3):
                    return "TERMINAL"
                message_id = job_id
                inbox = connection.execute(
                    "SELECT worker_id,lease_expires_at FROM inbox_messages WHERE message_id=%s FOR UPDATE",
                    (message_id,),
                ).fetchone()
                if inbox and inbox[1] is not None and inbox[1] > now and inbox[0] and str(inbox[0]) != self.worker_id:
                    return "OWNED_BY_OTHER_WORKER"
                if inbox:
                    connection.execute(
                        "UPDATE inbox_messages SET job_id=%s,worker_id=%s,lease_expires_at=now()+interval '30 minutes' WHERE message_id=%s",
                        (job_id, self.worker_id, message_id),
                    )
                else:
                    connection.execute(
                        "INSERT INTO inbox_messages(message_id,job_id,worker_id,lease_expires_at) VALUES(%s,%s,%s,now()+interval '30 minutes')",
                        (message_id, job_id, self.worker_id),
                    )
                connection.execute(
                    """UPDATE memory_jobs
                       SET status='RUNNING',stage='RESOLVING_ENTITIES',progress=10,
                           worker_id=%s,lease_expires_at=now()+interval '30 minutes',
                           last_heartbeat=now(),started_at=COALESCE(started_at,now()),
                           started_stage_at=now(),updated_at=now()
                       WHERE id=%s""",
                    (self.worker_id, job_id),
                )
                return "ACQUIRED"

    def renew(self, job_id: str) -> None:
        with self._connection() as connection:
            connection.execute(
                "UPDATE memory_jobs SET last_heartbeat=now(),lease_expires_at=now()+interval '30 minutes',updated_at=now() WHERE id=%s AND worker_id=%s AND status='RUNNING'",
                (job_id, self.worker_id),
            )
            connection.execute(
                "UPDATE inbox_messages SET lease_expires_at=now()+interval '30 minutes' WHERE message_id=%s AND worker_id=%s",
                (job_id, self.worker_id),
            )

    def _set_stage(self, job_id: str, stage: str, progress: int) -> None:
        with self._connection() as connection:
            connection.execute(
                "UPDATE memory_jobs SET stage=%s,progress=%s,started_stage_at=now(),last_heartbeat=now(),updated_at=now() WHERE id=%s AND worker_id=%s AND status='RUNNING'",
                (stage, max(0, min(100, int(progress))), job_id, self.worker_id),
            )

    @staticmethod
    def _fact_from_row(row: tuple[object, ...]) -> MemoryFact:
        evidence = row[11]
        if isinstance(evidence, str):
            try:
                evidence = json.loads(evidence)
            except json.JSONDecodeError:
                evidence = []
        evidence_ids = tuple(str(item) for item in (evidence or []) if item)
        return MemoryFact(
            fact_id=str(row[0]), owner_user_id=str(row[1]), meeting_id=str(row[2]),
            transcript_id=str(row[3]), transcript_version=int(row[4]), fact_type=str(row[5]),
            subject=str(row[6]) if row[6] else None, value=str(row[7] or ""),
            start_ms=int(row[8] or 0), end_ms=int(row[9] or 0), confidence=float(row[10] or 0),
            evidence_segment_ids=evidence_ids, derivation_type=str(row[12] or "EXPLICIT"),
            state=str(row[13] or "ACTIVE"), source_text=str(row[14] or ""), meeting_started_at=row[15],
        )

    @staticmethod
    def _scope_key(fact: MemoryFact) -> str | None:
        return canonical_topic_name(fact.subject) if fact.subject else None

    def _load_facts(self, payload: Mapping[str, object]) -> tuple[list[MemoryFact], set[str]]:
        job_id = str(payload["jobId"])
        with self._connection() as connection:
            identity = connection.execute(
                "SELECT owner_user_id,meeting_id,transcript_id,transcript_version FROM memory_jobs WHERE id=%s",
                (job_id,),
            ).fetchone()
            if identity is None or any(str(identity[index]) != str(payload[key]) for index, key in enumerate(("ownerUserId", "meetingId", "transcriptId", "transcriptVersion"))):
                raise ValueError("memory_payload_scope_mismatch")
            rows = connection.execute(
                """SELECT f.id,COALESCE(f.owner_user_id,m.owner_id),f.meeting_id,f.transcript_id,f.transcript_version,
                          f.fact_type,f.subject,f.value,f.start_ms,f.end_ms,f.confidence,
                          f.evidence_segment_ids,f.derivation_type,f.state,COALESCE(f.source_text,''),m.started_at
                     FROM transcript_facts f
                     JOIN meetings m ON m.id=f.meeting_id
                     JOIN memory_jobs j ON j.transcript_id=f.transcript_id AND j.transcript_version=f.transcript_version
                    WHERE j.id=%s AND COALESCE(f.owner_user_id,m.owner_id)=j.owner_user_id
                      AND f.transcript_id=%s AND f.transcript_version=%s
                      AND f.state='ACTIVE'""",
                (job_id, str(payload["transcriptId"]), int(payload["transcriptVersion"])),
            ).fetchall()
            # When a new transcript version invalidates facts, the old subjects
            # must be rebuilt too; otherwise an old thread can retain stale
            # metadata or disappear without being recomputed. Keep this query
            # on the same connection while the scope identity is still open.
            invalidated_rows = connection.execute(
                """SELECT f.subject
                     FROM transcript_facts f
                    WHERE f.meeting_id=%s AND f.invalidated_by_version=%s
                      AND f.subject IS NOT NULL""",
                (str(payload["meetingId"]), int(payload["transcriptVersion"])),
            ).fetchall()
        values = [self._fact_from_row(row) for row in rows]
        keys = {key for fact in values if (key := self._scope_key(fact))}
        keys.update(canonical_topic_name(str(row[0])) for row in invalidated_rows if row[0])
        return values, keys

    def _load_scope_in_transaction(
        self,
        connection: psycopg.Connection,
        payload: Mapping[str, object],
        keys: set[str],
    ) -> tuple[list[MemoryFact], dict[str, int]]:
        """Load the complete active owner/topic scope under advisory locks."""
        if not keys:
            return [], {}
        owner_id = str(payload["ownerUserId"])
        for key in sorted(keys):
            connection.execute("SELECT pg_advisory_xact_lock(hashtextextended(%s, 0))", (f"memory:{owner_id}:{key}",))
        rows = connection.execute(
            """SELECT f.id,COALESCE(f.owner_user_id,m.owner_id),f.meeting_id,f.transcript_id,f.transcript_version,
                      f.fact_type,f.subject,f.value,f.start_ms,f.end_ms,f.confidence,
                      f.evidence_segment_ids,f.derivation_type,f.state,COALESCE(f.source_text,''),m.started_at
                 FROM transcript_facts f
                 JOIN meetings m ON m.id=f.meeting_id
                WHERE COALESCE(f.owner_user_id,m.owner_id)=%s
                  AND f.state='ACTIVE'
                  AND f.subject IS NOT NULL
                  AND (f.subject_normalized = ANY(%s::text[]) OR f.subject_normalized IS NULL)
                ORDER BY m.started_at NULLS LAST,f.start_ms,f.id""",
            (owner_id, list(keys)),
        ).fetchall()
        values = []
        for row in rows:
            fact = self._fact_from_row(row)
            if self._scope_key(fact) in keys:
                values.append(fact)
        starts: dict[str, object] = {}
        for fact in values:
            starts[fact.meeting_id] = fact.meeting_started_at
        ordered = sorted(starts, key=lambda meeting_id: (starts[meeting_id] is None, starts[meeting_id], meeting_id))
        return values, {meeting_id: index for index, meeting_id in enumerate(ordered)}

    def process(self, payload: Mapping[str, object]) -> MemoryIndexResult:
        job_id = str(payload["jobId"])
        _current_facts, subject_keys = self._load_facts(payload)
        if len(subject_keys) > MEMORY_MAX_SUBJECTS_PER_JOB:
            return self._complete_scope_review(job_id, "MEMORY_SCOPE_LIMIT_EXCEEDED")
        self._set_stage(job_id, "LINKING_FACTS", 35)
        with self._connection() as connection:
            with connection.transaction():
                facts, meeting_order = self._load_scope_in_transaction(connection, payload, subject_keys)
                if len(facts) > MEMORY_MAX_FACTS_PER_JOB or any(
                    sum(1 for fact in facts if self._scope_key(fact) == key) > MEMORY_MAX_FACTS_PER_SUBJECT
                    for key in subject_keys
                ):
                    return self._complete_scope_review_in_transaction(connection, job_id, "MEMORY_SCOPE_LIMIT_EXCEEDED")
                self._set_stage(job_id, "BUILDING_THREADS", 70)
                result = MemoryIndexWorker().index(facts, meeting_order)
                if result.index is None:
                    return result
                fact_ids = [fact.fact_id for fact in result.index.facts if fact.fact_id]
                if fact_ids:
                    connection.execute(
                        "UPDATE memory_fact_relations SET invalidated_at=COALESCE(invalidated_at,now()) WHERE invalidated_at IS NULL AND (source_fact_id=ANY(%s::uuid[]) OR target_fact_id=ANY(%s::uuid[]))",
                        (fact_ids, fact_ids),
                    )
                entity_ids: dict[tuple[str, str], str] = {}
                for entity in result.index.entities:
                    row = connection.execute(
                        """INSERT INTO memory_entities(owner_user_id,entity_type,canonical_name,normalized_name,aliases)
                           VALUES(%s,%s,%s,%s,%s::jsonb)
                           ON CONFLICT(owner_user_id,entity_type,normalized_name) DO UPDATE
                             SET canonical_name=EXCLUDED.canonical_name,
                                 aliases=(COALESCE(memory_entities.aliases,'[]'::jsonb) || EXCLUDED.aliases),updated_at=now()
                           RETURNING id""",
                        (str(payload["ownerUserId"]), entity.entity_type, entity.canonical_name, entity.normalized_name, json.dumps(list(entity.aliases))),
                    ).fetchone()
                    entity_ids[(entity.entity_type, entity.normalized_name)] = str(row[0])
                for fact in result.index.facts:
                    if not fact.fact_id:
                        continue
                    connection.execute("DELETE FROM fact_entities WHERE fact_id=%s", (fact.fact_id,))
                    if fact.subject:
                        topic_normalized = canonical_topic_name(fact.subject)
                        normalized = normalize_entity_name(fact.subject)
                        topic_id = entity_ids.get(("TOPIC", topic_normalized))
                        if topic_id:
                            connection.execute("INSERT INTO fact_entities(fact_id,entity_id,role,confidence) VALUES(%s,%s,'SUBJECT',%s) ON CONFLICT DO NOTHING", (fact.fact_id, topic_id, fact.confidence))
                        # Preserve explicit equipment/project/location links
                        # without replacing the canonical TOPIC scope.
                        for entity in result.index.entities:
                            if entity.entity_type in {"EQUIPMENT", "PROJECT", "LOCATION"} and entity.normalized_name == normalized:
                                typed_id = entity_ids.get((entity.entity_type, normalized))
                                if typed_id:
                                    connection.execute("INSERT INTO fact_entities(fact_id,entity_id,role,confidence) VALUES(%s,%s,'MENTION',%s) ON CONFLICT DO NOTHING", (fact.fact_id, typed_id, fact.confidence))
                    if fact.fact_type == "RESPONSIBLE":
                        entity_id = entity_ids.get(("PERSON", normalize_entity_name(fact.value)))
                        if entity_id:
                            connection.execute("INSERT INTO fact_entities(fact_id,entity_id,role,confidence) VALUES(%s,%s,'RESPONSIBLE',%s) ON CONFLICT DO NOTHING", (fact.fact_id, entity_id, fact.confidence))
                for relation in result.index.relations:
                    connection.execute(
                        """INSERT INTO memory_fact_relations(source_fact_id,target_fact_id,relation_type,confidence,derivation_type,invalidated_at)
                           VALUES(%s,%s,%s,%s,%s,NULL) ON CONFLICT(source_fact_id,target_fact_id,relation_type) DO UPDATE
                             SET confidence=EXCLUDED.confidence,derivation_type=EXCLUDED.derivation_type,invalidated_at=NULL""",
                        (relation.source_fact_id, relation.target_fact_id, relation.relation_type, relation.confidence, relation.derivation_type),
                    )
                for fact in result.index.facts:
                    if fact.fact_id and fact.subject:
                        connection.execute(
                            "UPDATE transcript_facts SET subject_normalized=%s WHERE id=%s AND subject_normalized IS DISTINCT FROM %s",
                            (self._scope_key(fact), fact.fact_id, self._scope_key(fact)),
                        )
                superseded = {item.source_fact_id for item in result.index.relations if item.relation_type == "SUPERSEDES"}
                closed = {
                    fact_id
                    for item in result.index.relations
                    if item.relation_type == "CLOSES"
                    for fact_id in (item.source_fact_id, item.target_fact_id)
                }
                rebuilt_keys = {thread.normalized_title for thread in result.index.threads}
                for thread in result.index.threads:
                    row = connection.execute(
                        """INSERT INTO memory_threads(owner_user_id,title,normalized_title,state,first_seen_at,last_seen_at,updated_at)
                           VALUES(%s,%s,%s,%s,%s,%s,now())
                           ON CONFLICT(owner_user_id,normalized_title) DO UPDATE SET title=EXCLUDED.title,state=EXCLUDED.state,first_seen_at=EXCLUDED.first_seen_at,last_seen_at=EXCLUDED.last_seen_at,updated_at=now()
                           RETURNING id""",
                        (str(payload["ownerUserId"]), thread.title, thread.normalized_title, thread.state, thread.first_seen_at, thread.last_seen_at),
                    ).fetchone()
                    thread_id = str(row[0])
                    connection.execute("DELETE FROM memory_thread_facts WHERE thread_id=%s", (thread_id,))
                    for sequence, fact_id in enumerate(thread.fact_ids):
                        role = "HISTORY" if fact_id in superseded else "CLOSED" if fact_id in closed else "CURRENT"
                        connection.execute("INSERT INTO memory_thread_facts(thread_id,fact_id,sequence,role) VALUES(%s,%s,%s,%s) ON CONFLICT(thread_id,fact_id) DO UPDATE SET sequence=EXCLUDED.sequence,role=EXCLUDED.role", (thread_id, fact_id, sequence, role))
                for missing_key in subject_keys - rebuilt_keys:
                    empty = connection.execute(
                        "SELECT id FROM memory_threads WHERE owner_user_id=%s AND normalized_title=%s FOR UPDATE",
                        (str(payload["ownerUserId"]), missing_key),
                    ).fetchone()
                    if empty:
                        connection.execute("DELETE FROM memory_thread_facts WHERE thread_id=%s", (empty[0],))
                        connection.execute("DELETE FROM memory_threads WHERE id=%s", (empty[0],))
                status = result.status
                connection.execute(
                    "UPDATE memory_jobs SET status=%s,stage=%s,progress=100,error_code=%s,error_message=%s,completed_at=CASE WHEN %s IN ('READY','NEEDS_REVIEW') THEN now() ELSE NULL END,worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now() WHERE id=%s AND worker_id=%s",
                    (status, result.stage, result.error_code, result.error_code, status, job_id, self.worker_id),
                )
                connection.execute("DELETE FROM inbox_messages WHERE message_id=%s AND worker_id=%s", (job_id, self.worker_id))
        return result

    def _complete_scope_review(self, job_id: str, error_code: str) -> MemoryIndexResult:
        with self._connection() as connection:
            with connection.transaction():
                return self._complete_scope_review_in_transaction(connection, job_id, error_code)

    @staticmethod
    def _complete_scope_review_in_transaction(connection: psycopg.Connection, job_id: str, error_code: str) -> MemoryIndexResult:
        result = MemoryIndexResult("NEEDS_REVIEW", "NEEDS_REVIEW", MemoryIndex((), (), (), ()), error_code)
        connection.execute(
            """UPDATE memory_jobs
                  SET status='NEEDS_REVIEW',stage='NEEDS_REVIEW',progress=100,error_code=%s,error_message=%s,
                      completed_at=now(),worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now()
                WHERE id=%s""",
            (error_code, error_code, job_id),
        )
        connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (job_id,))
        return result

    def schedule_retry(self, job_id: str, error_code: str) -> tuple[bool, int]:
        with self._connection() as connection:
            row = connection.execute(
                """UPDATE memory_jobs SET attempt=attempt+1,
                         status=CASE WHEN attempt+1 >= max_attempts THEN 'FAILED' ELSE 'QUEUED' END,
                         stage=CASE WHEN attempt+1 >= max_attempts THEN 'FAILED' ELSE 'QUEUED' END,
                         progress=0,error_code=%s,error_message=%s,
                         next_retry_at=CASE WHEN attempt+1 >= max_attempts THEN NULL ELSE now()+((LEAST(300,5*power(2,attempt))) * interval '1 second') END,
                         worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now()
                       WHERE id=%s RETURNING status,attempt""",
                (error_code[:120], error_code[:500], job_id),
            ).fetchone()
            connection.execute("DELETE FROM inbox_messages WHERE message_id=%s", (job_id,))
            return (str(row[0]).upper() != "FAILED", int(row[1])) if row else (False, 0)


async def run() -> None:
    try:
        import nats
    except ImportError as exc:  # pragma: no cover - image dependency contract
        raise RuntimeError("Install workers/memory_worker/requirements.txt") from exc
    client = await nats.connect(os.getenv("NATS_URL", "nats://nats:4222"))
    repository = MemoryProjectionRepository()
    heartbeat = AsyncHeartbeat("memory-worker", capabilities=lambda: {"memoryIndex": "ready", "cpuOnly": True})
    await heartbeat.start()
    jetstream = client.jetstream()
    await ensure_stream(jetstream, name="WHISPERX", subjects=["media.ingest", "ml.transcribe", "llm.summarize", "llm.assistant", "memory.index"])
    subscription = await jetstream.pull_subscribe("memory.index", durable="memory-worker")
    heartbeat.set_state("READY")
    while True:
        for message in await fetch_available(subscription, nats.errors.TimeoutError, timeout=5):
            job_id: str | None = None
            try:
                payload = json.loads(message.data)
                required = ("jobId", "meetingId", "transcriptId", "transcriptVersion", "ownerUserId")
                if not isinstance(payload, dict) or any(not str(payload.get(key, "")).strip() for key in required):
                    raise ValueError("memory_payload_invalid")
                job_id = str(payload["jobId"])
                claim = await asyncio.to_thread(repository.claim, job_id)
                if claim in {"TERMINAL"}:
                    await message.ack()
                    continue
                if claim in {"OWNED_BY_OTHER_WORKER", "WAITING"}:
                    await message.nak(delay=5)
                    continue
                heartbeat.set_job(job_id)
                heartbeat.set_state("BUSY")
                async with maintain_message(message, on_tick=lambda: asyncio.to_thread(repository.renew, job_id)):
                    result = await asyncio.to_thread(repository.process, payload)
                if result.status in {"READY", "NEEDS_REVIEW"}:
                    await message.ack()
                else:
                    retryable, _attempt = await asyncio.to_thread(repository.schedule_retry, job_id, result.error_code or "MEMORY_INDEX_FAILED")
                    await message.nak(delay=5 if retryable else 30)
            except ValueError as exc:
                LOGGER.warning("memory_poison_message_discarded reason=%s", str(exc))
                await message.ack()
            except Exception:
                LOGGER.exception("memory_index_message_failed")
                if job_id:
                    try:
                        retryable, _attempt = await asyncio.to_thread(repository.schedule_retry, job_id, "MEMORY_INDEX_RUNTIME_FAILED")
                        await message.nak(delay=10 if retryable else 30)
                    except Exception:
                        await message.nak(delay=30)
                else:
                    await message.nak(delay=10)
            finally:
                heartbeat.set_job(None)
                heartbeat.set_state("READY")


if __name__ == "__main__":
    asyncio.run(run())
