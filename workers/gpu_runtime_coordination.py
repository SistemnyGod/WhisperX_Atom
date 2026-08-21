"""Durable coordination between ASR and resident LLM runtimes.

The PostgreSQL row is deliberately tiny and single-tenant.  It is not a
second GPU lock: ``PostgresGpuLease`` remains the exclusion primitive.  This
row communicates an ASR preemption request to the summary/assistant process,
which can unload llama-server before ASR acquires the advisory lock.
"""

from __future__ import annotations

import logging
import os
import time
from typing import Any

import psycopg


LOGGER = logging.getLogger("whisperx.gpu-runtime-coordination")


class GpuRuntimeCoordinator:
    def __init__(self, conninfo: str | None = None) -> None:
        self.conninfo = conninfo or os.getenv("DATABASE_URL", "")
        self.enabled = os.getenv("GPU_RUNTIME_COORDINATION_ENABLED", "true").lower() in {"1", "true", "yes"}

    def _connect(self) -> psycopg.Connection[Any]:
        if not self.conninfo:
            raise RuntimeError("DATABASE_URL is not configured")
        return psycopg.connect(self.conninfo, autocommit=True)

    def request_asr(self, request_id: str, owner: str = "gpu-worker") -> bool:
        """Publish an ASR request, without clobbering a newer request."""
        if not self.enabled:
            return True
        try:
            job_id = request_id.split(":", 1)[0]
            try:
                import uuid
                parsed_job_id: Any = uuid.UUID(job_id)
            except (ValueError, TypeError):
                parsed_job_id = None
            with self._connect() as connection:
                connection.execute(
                    """
                    INSERT INTO gpu_runtime_coordination
                        (id, asr_state, asr_request_id, asr_job_id, asr_owner, asr_requested_at, updated_at)
                    VALUES (1, 'ASR_PENDING', %s, %s, %s, now(), now())
                    ON CONFLICT (id) DO UPDATE SET
                        asr_state='ASR_PENDING',
                        asr_request_id=EXCLUDED.asr_request_id,
                        asr_job_id=EXCLUDED.asr_job_id,
                        asr_owner=EXCLUDED.asr_owner,
                        asr_requested_at=EXCLUDED.asr_requested_at,
                        updated_at=now()
                    """,
                    (request_id, parsed_job_id, owner),
                )
            return True
        except Exception:
            # Existing deployments can roll forward before migration 040 is
            # applied.  The advisory GPU lease and resident-process probe stay
            # active, so a missing coordination table is safe but observable.
            LOGGER.warning("gpu_runtime_coordination_request_unavailable", exc_info=True)
            return False

    def asr_request_active(self) -> bool:
        if not self.enabled:
            return False
        try:
            with self._connect() as connection:
                row = connection.execute(
                    """
                    SELECT c.asr_state,c.asr_request_id,j.status,j.lease_expires_at
                    FROM gpu_runtime_coordination c
                    LEFT JOIN jobs j ON j.id=c.asr_job_id
                    WHERE c.id=1
                    """
                ).fetchone()
                active = bool(row and str(row[0] or "IDLE") == "ASR_PENDING" and row[1]
                              and str(row[2] or "") == "RUNNING"
                              and row[3] is not None and row[3].timestamp() > time.time())
                if row and row[1] and not active:
                    connection.execute(
                        "UPDATE gpu_runtime_coordination SET asr_state='IDLE',asr_request_id=NULL,asr_job_id=NULL,asr_owner=NULL,asr_requested_at=NULL,updated_at=now() WHERE id=1"
                    )
            return active
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_read_unavailable", exc_info=True)
            return False

    def llm_may_start(self) -> bool:
        """Return false while a durable ASR request is pending."""
        return not self.asr_request_active()

    def mark_llm_resident(self, owner: str) -> bool:
        if not self.enabled:
            return True
        try:
            with self._connect() as connection:
                row = connection.execute(
                    """
                    UPDATE gpu_runtime_coordination
                    SET llm_state='RESIDENT', llm_owner=%s, updated_at=now()
                    WHERE id=1 AND asr_request_id IS NULL
                    RETURNING id
                    """,
                    (owner,),
                ).fetchone()
            return bool(row)
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_mark_resident_unavailable", exc_info=True)
            # Fall back to the existing advisory lock/probe until migration
            # 040 is present; an unavailable coordination table is not proof
            # that ASR is active.
            return True

    def acknowledge_llm_release(self, request_id: str) -> None:
        if not self.enabled:
            return
        try:
            with self._connect() as connection:
                connection.execute(
                    """
                    UPDATE gpu_runtime_coordination
                    SET llm_state='STOPPED', llm_owner=NULL,
                        llm_ack_request_id=%s, updated_at=now()
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
                    SET llm_state='STOPPED', llm_owner=NULL, updated_at=now()
                    WHERE id=1 AND (llm_owner=%s OR llm_owner IS NULL)
                    """,
                    (owner,),
                )
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_mark_stopped_unavailable", exc_info=True)

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
                        asr_job_id=NULL, asr_requested_at=NULL, updated_at=now()
                    WHERE id=1 AND asr_request_id=%s
                    """,
                    (request_id,),
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
                    SELECT asr_request_id, llm_state
                    FROM gpu_runtime_coordination WHERE id=1
                    """
                ).fetchone()
            request_id = str(row[0]) if row and row[0] else ""
            if not request_id or str(row[1] or "STOPPED") != "RESIDENT":
                return False
            runtime.stop()
            self.acknowledge_llm_release(request_id)
            LOGGER.info("resident_llm_preempted owner=%s request_id=%s", owner, request_id)
            return True
        except Exception:
            LOGGER.warning("gpu_runtime_coordination_preempt_unavailable", exc_info=True)
            return False
