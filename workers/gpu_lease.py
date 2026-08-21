from __future__ import annotations

import os
import time
from typing import Any

import psycopg


class AssistantGpuBusy(TimeoutError):
    """Assistant should remain queued while ASR owns the single GPU."""

    def __init__(self, reason: str = "ASSISTANT_WAITING_FOR_GPU") -> None:
        super().__init__(reason)
        self.reason = reason


class PostgresGpuLease:
    """Cross-container GPU mutex backed by a PostgreSQL advisory lock.

    The connection must remain open for the entire inference window: PostgreSQL
    releases the lock automatically if a worker process dies or the connection
    drops. This prevents two Python workers from loading CUDA models together.
    """

    def __init__(self, conninfo: str | None = None, key: str | None = None, wait_seconds: int | None = None, priority: int = 100) -> None:
        self._conninfo = conninfo or os.getenv("DATABASE_URL", "")
        self._key = key or os.getenv("GPU_LEASE_KEY", "whisperx-atom-gpu-0")
        # Lower values have precedence: ASR (10) > Assistant (30) >
        # enrichment/V2 (50) > Summary (100).
        self._priority = max(0, int(priority))
        if wait_seconds is not None:
            self._wait_seconds = wait_seconds
        elif self._priority == 30:
            self._wait_seconds = int(os.getenv("ASSISTANT_GPU_LEASE_WAIT_SECONDS", "10"))
        else:
            self._wait_seconds = int(os.getenv("GPU_LEASE_WAIT_SECONDS", "1800"))
        self._connection: psycopg.Connection[Any] | None = None

    def _higher_priority_pending(self, connection: psycopg.Connection[Any]) -> bool:
        """Avoid letting a lower-priority workload jump ahead of queued work.

        The advisory lock still provides the cross-process exclusion. This
        small database gate only applies before attempting the lock, so a
        crashed worker cannot strand the queue: RUNNING jobs count only while
        their lease is fresh and assistant requests use the durable QUEUED
        state. ASR workers (priority 10) never wait on this gate.
        """
        freshness_seconds = max(5, int(os.getenv("GPU_PRIORITY_RUNNING_FRESHNESS_SECONDS", "90")))
        if self._priority > 10:
            asr_pending = connection.execute(
                """
                SELECT EXISTS(
                    SELECT 1 FROM jobs
                    WHERE type IN ('TRANSCRIBE','TRANSCRIBE_ASR','TRANSCRIBE_REPROCESS')
                      AND (status='QUEUED'
                           OR (status='RUNNING'
                              AND lease_expires_at IS NOT NULL
                              AND lease_expires_at > now()
                              AND COALESCE(progress_changed_at, stage_changed_at, last_heartbeat, updated_at)
                                  >= now() - (%s * interval '1 second'))
                      )
                )
                """,
                (freshness_seconds,),
            ).fetchone()[0]
            if asr_pending:
                return True

        if self._priority > 30:
            assistant_pending = connection.execute(
                """
                SELECT EXISTS(
                    SELECT 1 FROM assistant_queries q
                    WHERE q.created_at >= now() - interval '60 minutes'
                      AND (
                          (q.status='QUEUED' AND (q.next_retry_at IS NULL OR q.next_retry_at <= now()))
                          OR (q.status='RUNNING' AND (
                              q.updated_at >= now() - (%s * interval '1 second')
                              OR EXISTS (
                                  SELECT 1 FROM inbox_messages i
                                  WHERE i.job_id=q.id AND i.lease_expires_at > now()
                              )
                          ))
                      )
                )
                """,
                (freshness_seconds,),
            ).fetchone()[0]
            if assistant_pending:
                return True

        if self._priority > 50:
            enrichment_pending = connection.execute(
                """
                SELECT EXISTS(
                    SELECT 1 FROM jobs
                    WHERE type='TRANSCRIPT_ENRICH'
                      AND status IN ('QUEUED','RUNNING')
                      AND (
                          status='QUEUED'
                          OR (lease_expires_at IS NOT NULL
                              AND lease_expires_at > now()
                              AND COALESCE(progress_changed_at, stage_changed_at, last_heartbeat, updated_at)
                                  >= now() - (%s * interval '1 second'))
                      )
                )
                """,
                (freshness_seconds,),
            ).fetchone()[0]
            if enrichment_pending:
                return True
        return False

    def acquire(self) -> None:
        connection = psycopg.connect(self._conninfo, autocommit=True)
        deadline = time.monotonic() + max(1, self._wait_seconds)
        try:
            while True:
                if self._higher_priority_pending(connection):
                    if time.monotonic() >= deadline:
                        if self._priority == 30:
                            raise AssistantGpuBusy()
                        raise TimeoutError("gpu_lease_priority_timeout")
                    time.sleep(min(2.0, max(0.1, deadline - time.monotonic())))
                    continue
                locked = connection.execute(
                    "SELECT pg_try_advisory_lock(hashtextextended(%s, 0))",
                    (self._key,),
                ).fetchone()[0]
                if locked:
                    self._connection = connection
                    return
                if time.monotonic() >= deadline:
                    if self._priority == 30:
                        raise AssistantGpuBusy("ASSISTANT_WAITING_FOR_GPU")
                    raise TimeoutError("gpu_lease_timeout")
                time.sleep(min(2.0, max(0.1, deadline - time.monotonic())))
        except BaseException:
            connection.close()
            raise

    def release(self) -> None:
        connection = self._connection
        self._connection = None
        if connection is None:
            return
        try:
            connection.execute("SELECT pg_advisory_unlock(hashtextextended(%s, 0))", (self._key,))
        finally:
            connection.close()

    async def __aenter__(self) -> "PostgresGpuLease":
        import asyncio

        await asyncio.to_thread(self.acquire)
        return self

    async def __aexit__(self, *_: object) -> None:
        import asyncio

        await asyncio.to_thread(self.release)
