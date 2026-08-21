"""Durable coordination between ASR and resident LLM runtimes.

The PostgreSQL row is deliberately tiny and single-tenant.  It is not a
second GPU lock: ``PostgresGpuLease`` remains the exclusion primitive.  This
row communicates an ASR preemption request to the summary/assistant process,
which can unload llama-server before ASR acquires the advisory lock.
"""

from __future__ import annotations

import logging
import os
import socket
import time
import uuid
from typing import Any

import psycopg


LOGGER = logging.getLogger("whisperx.gpu-runtime-coordination")


class GpuRuntimeCoordinator:
    def __init__(self, conninfo: str | None = None, owner: str | None = None) -> None:
        self.conninfo = conninfo or os.getenv("DATABASE_URL", "")
        self.enabled = os.getenv("GPU_RUNTIME_COORDINATION_ENABLED", "true").lower() in {"1", "true", "yes"}
        self._lease_key = os.getenv("GPU_LEASE_KEY", "whisperx-atom-gpu-0")
        self.owner = owner or f"summary-runtime:{socket.gethostname()}:{os.getpid()}:{uuid.uuid4().hex[:12]}"

    def _connect(self) -> psycopg.Connection[Any]:
        if not self.conninfo:
            raise RuntimeError("DATABASE_URL is not configured")
        return psycopg.connect(self.conninfo, autocommit=True)

    def request_workload(self, request_id: str, workload_type: str, priority: int, owner: str = "gpu-worker") -> bool:
        """Publish a priority-aware workload request for idle LLM preemption."""
        if not self.enabled:
            return True
        workload_type = str(workload_type or "V1_ASR").upper()
        priority = max(0, int(priority))
        try:
            job_id = request_id.split(":", 1)[0]
            try:
                import uuid
                parsed_job_id: Any = uuid.UUID(job_id)
            except (ValueError, TypeError):
                parsed_job_id = None
            with self._connect() as connection:
                result = connection.execute(
                    """
                    INSERT INTO gpu_runtime_coordination
                        (id, asr_state, asr_request_id, asr_job_id, asr_owner, asr_requested_at,
                         workload_type, workload_priority, workload_request_id, workload_job_id,
                         workload_owner, workload_requested_at, updated_at)
                    VALUES (1, 'ASR_PENDING', %s, %s, %s, now(), %s, %s, %s, %s, %s, now(), now())
                    ON CONFLICT (id) DO UPDATE SET
                        asr_state='ASR_PENDING',
                        asr_request_id=EXCLUDED.asr_request_id,
                        asr_job_id=EXCLUDED.asr_job_id,
                        asr_owner=EXCLUDED.asr_owner,
                        asr_requested_at=EXCLUDED.asr_requested_at,
                        workload_type=EXCLUDED.workload_type,
                        workload_priority=EXCLUDED.workload_priority,
                        workload_request_id=EXCLUDED.workload_request_id,
                        workload_job_id=EXCLUDED.workload_job_id,
                        workload_owner=EXCLUDED.workload_owner,
                        workload_requested_at=EXCLUDED.workload_requested_at,
                        updated_at=now()
                    WHERE gpu_runtime_coordination.workload_request_id IS NULL
                       OR gpu_runtime_coordination.workload_priority >= EXCLUDED.workload_priority
                    """,
                    (request_id, parsed_job_id, owner, workload_type, priority, request_id, parsed_job_id, owner),
                )
            return result.rowcount > 0
        except Exception:
            # Existing deployments can roll forward before migration 040 is
            # applied.  The advisory GPU lease and resident-process probe stay
            # active, so a missing coordination table is safe but observable.
            LOGGER.warning("gpu_runtime_coordination_request_unavailable", exc_info=True)
            return False

    def request_asr(self, request_id: str, owner: str = "gpu-worker") -> bool:
        """Compatibility wrapper for older workers and rolling upgrades."""
        return self.request_workload(request_id, "V1_ASR", 10, owner)

    def asr_request_active(self) -> bool:
        if not self.enabled:
            return False
        try:
            with self._connect() as connection:
                row = connection.execute(
                    """
                    SELECT COALESCE(c.workload_request_id,c.asr_request_id),
                           COALESCE(c.workload_job_id,c.asr_job_id),j.status,j.lease_expires_at
                    FROM gpu_runtime_coordination c
                    LEFT JOIN jobs j ON j.id=COALESCE(c.workload_job_id,c.asr_job_id)
                    WHERE c.id=1
                    """
                ).fetchone()
                active = bool(row and row[0] and str(row[2] or "") == "RUNNING"
                              and row[3] is not None and row[3].timestamp() > time.time())
                if row and row[0] and not active:
                    connection.execute(
                        """UPDATE gpu_runtime_coordination
                           SET asr_state='IDLE',asr_request_id=NULL,asr_job_id=NULL,asr_owner=NULL,asr_requested_at=NULL,
                               workload_type=NULL,workload_priority=NULL,workload_request_id=NULL,workload_job_id=NULL,
                               workload_owner=NULL,workload_requested_at=NULL,updated_at=now() WHERE id=1"""
                    )
            return active
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_read_unavailable", exc_info=True)
            return False

    def llm_may_start(self) -> bool:
        """Return false while a durable ASR request is pending."""
        return not self.asr_request_active()

    def mark_llm_resident(self, owner: str, workload: str | None = None, request_id: str | None = None) -> bool:
        """Claim resident ownership after the caller holds the GPU lease.

        Summary/Assistant invoke this only inside ``PostgresGpuLease``. That
        ordering is the advisory-lock gate for normal ownership changes. A
        dead process is recovered separately by ``reclaim_stale_owner`` before
        consumers start; an active generation remains protected by ``llm_active``.
        """
        if not self.enabled:
            return True
        try:
            with self._connect() as connection:
                row = connection.execute(
                    """
                    UPDATE gpu_runtime_coordination
                    SET llm_state='RESIDENT', llm_owner=%s, llm_active=FALSE,
                        llm_owner_heartbeat_at=now(), llm_active_workload=%s,
                        llm_active_request_id=%s, updated_at=now()
                    WHERE id=1 AND (llm_owner=%s OR llm_owner IS NULL
                                    OR (NOT COALESCE(llm_active,FALSE)
                                        AND (llm_owner_heartbeat_at IS NULL
                                             OR llm_owner_heartbeat_at < now() - (%s * interval '1 second'))))
                    RETURNING workload_request_id,asr_request_id
                    """,
                    (owner, workload, request_id, owner, float(os.getenv("GPU_LLM_OWNER_STALE_SECONDS", "30"))),
                ).fetchone()
            return bool(row) and row[0] is None and row[1] is None
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_mark_resident_unavailable", exc_info=True)
            # Fall back to the existing advisory lock/probe until migration
            # 040 is present; an unavailable coordination table is not proof
            # that ASR is active.
            return True

    def reclaim_stale_owner(self) -> bool:
        """Clear ownership from a dead process only when the GPU lock is free.

        This runs during process startup, before any workload acquires the
        lease. If another process is still generating, ``pg_try_advisory_lock``
        fails and the active owner is left untouched.
        """
        if not self.enabled:
            return False
        try:
            with self._connect() as connection:
                locked = bool(connection.execute(
                    "SELECT pg_try_advisory_lock(hashtextextended(%s, 0))",
                    (self._lease_key,),
                ).fetchone()[0])
                if not locked:
                    return False
                try:
                    row = connection.execute(
                        """
                        UPDATE gpu_runtime_coordination
                        SET llm_state='STOPPED', llm_owner=NULL, llm_active=FALSE,
                            llm_owner_heartbeat_at=NULL, llm_active_workload=NULL,
                            llm_active_request_id=NULL, updated_at=now()
                        WHERE id=1
                          AND llm_owner IS NOT NULL
                          AND llm_owner_heartbeat_at < now() - (%s * interval '1 second')
                        RETURNING llm_owner
                        """,
                        (float(os.getenv("GPU_LLM_OWNER_STALE_SECONDS", "30")),),
                    ).fetchone()
                    return bool(row)
                finally:
                    connection.execute(
                        "SELECT pg_advisory_unlock(hashtextextended(%s, 0))",
                        (self._lease_key,),
                    )
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_reclaim_unavailable", exc_info=True)
            return False

    def mark_llm_busy(self, owner: str, workload: str | None = None, request_id: str | None = None) -> None:
        """Mark an in-flight generation so ASR cannot kill it mid-response."""
        if not self.enabled:
            return
        try:
            with self._connect() as connection:
                connection.execute(
                    """UPDATE gpu_runtime_coordination
                       SET llm_state='RESIDENT',llm_owner=%s,llm_active=TRUE,
                           llm_owner_heartbeat_at=now(),llm_active_workload=%s,
                           llm_active_request_id=%s,updated_at=now()
                       WHERE id=1 AND llm_owner=%s""",
                    (owner, workload, request_id, owner),
                )
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_mark_busy_unavailable", exc_info=True)

    def acknowledge_llm_release(self, request_id: str) -> None:
        if not self.enabled:
            return
        try:
            with self._connect() as connection:
                connection.execute(
                    """
                    UPDATE gpu_runtime_coordination
                    SET llm_state='STOPPED', llm_owner=NULL, llm_active=FALSE,
                        llm_ack_request_id=%s, llm_owner_heartbeat_at=NULL,
                        llm_active_workload=NULL, llm_active_request_id=NULL,
                        updated_at=now()
                    WHERE id=1
                    """,
                    (request_id,),
                )
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_ack_unavailable", exc_info=True)

    def mark_llm_stopped(self, owner: str) -> None:
        if not self.enabled:
            return
        try:
            with self._connect() as connection:
                connection.execute(
                    """
                    UPDATE gpu_runtime_coordination
                        SET llm_state='STOPPED', llm_owner=NULL, llm_active=FALSE,
                            llm_owner_heartbeat_at=NULL, llm_active_workload=NULL,
                            llm_active_request_id=NULL, updated_at=now()
                    WHERE id=1 AND (llm_owner=%s OR llm_owner IS NULL)
                    """,
                    (owner,),
                )
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_mark_stopped_unavailable", exc_info=True)

    def heartbeat_owner(self, owner: str) -> bool:
        """Refresh only this process' ownership; never resurrect another owner."""
        if not self.enabled:
            return True
        try:
            with self._connect() as connection:
                row = connection.execute(
                    """UPDATE gpu_runtime_coordination
                       SET llm_owner_heartbeat_at=now(), updated_at=now()
                       WHERE id=1 AND llm_owner=%s
                       RETURNING llm_owner""",
                    (owner,),
                ).fetchone()
            return bool(row)
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_heartbeat_unavailable", exc_info=True)
            return False

    def wait_for_llm_release(self, request_id: str, timeout_seconds: float = 120.0) -> bool:
        """Wait until the resident LLM is stopped or this request is acked."""
        if not self.enabled:
            return True
        deadline = time.monotonic() + max(1.0, timeout_seconds)
        while time.monotonic() < deadline:
            try:
                with self._connect() as connection:
                    row = connection.execute(
                        """
                        SELECT llm_state, llm_ack_request_id, asr_request_id
                        FROM gpu_runtime_coordination WHERE id=1
                        """
                    ).fetchone()
                if row is None or str(row[1] or "") == request_id or str(row[0] or "STOPPED") != "RESIDENT":
                    return True
            except Exception:
                LOGGER.warning("gpu_runtime_coordination_wait_unavailable", exc_info=True)
                return True
            time.sleep(1.0)
        return False

    def clear_asr(self, request_id: str) -> None:
        if not self.enabled:
            return
        try:
            with self._connect() as connection:
                connection.execute(
                    """
                    UPDATE gpu_runtime_coordination
                    SET asr_state='IDLE', asr_request_id=NULL, asr_owner=NULL,
                        asr_job_id=NULL, asr_requested_at=NULL,
                        workload_type=NULL, workload_priority=NULL, workload_request_id=NULL,
                        workload_job_id=NULL, workload_owner=NULL, workload_requested_at=NULL, updated_at=now()
                    WHERE id=1 AND (asr_request_id=%s OR workload_request_id=%s)
                    """,
                    (request_id, request_id),
                )
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_clear_unavailable", exc_info=True)

    def preempt_if_requested(self, runtime: Any, owner: str) -> bool:
        """Unload resident llama when ASR has published a request."""
        if not self.enabled:
            return False
        try:
            with self._connect() as connection:
                row = connection.execute(
                    """
                    SELECT COALESCE(workload_request_id,asr_request_id), llm_state, llm_active
                    FROM gpu_runtime_coordination WHERE id=1
                    """
                ).fetchone()
            request_id = str(row[0]) if row and row[0] else ""
            if not request_id or str(row[1] or "STOPPED") != "RESIDENT" or bool(row[2]):
                return False
            runtime.stop()
            self.acknowledge_llm_release(request_id)
            LOGGER.info("resident_llm_preempted owner=%s request_id=%s", owner, request_id)
            return True
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_preempt_unavailable", exc_info=True)
            return False
