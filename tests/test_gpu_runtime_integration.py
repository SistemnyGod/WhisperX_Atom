"""PostgreSQL integration checks for the process-scoped Qwen owner."""

from __future__ import annotations

import os

import pytest

psycopg = pytest.importorskip("psycopg")
from workers.gpu_runtime_coordination import GpuRuntimeCoordinator


pytestmark = pytest.mark.integration


@pytest.fixture()
def coordination_database():
    if not os.getenv("CI") and not os.getenv("WHISPERX_TEST_DATABASE"):
        pytest.skip("integration database is enabled only in CI or explicit test runs")
    conninfo = os.getenv("DATABASE_URL") or os.getenv("WHISPERX_TEST_DATABASE")
    if not conninfo:
        pytest.skip("DATABASE_URL is not configured")
    with psycopg.connect(conninfo, autocommit=True) as connection:
        connection.execute("DROP TABLE IF EXISTS gpu_runtime_coordination")
        connection.execute(
            """
            CREATE TABLE gpu_runtime_coordination (
                id INTEGER PRIMARY KEY,
                asr_state TEXT,
                asr_request_id TEXT,
                asr_job_id UUID,
                asr_owner TEXT,
                asr_requested_at TIMESTAMPTZ,
                workload_type TEXT,
                workload_priority INTEGER,
                workload_request_id TEXT,
                workload_job_id UUID,
                workload_owner TEXT,
                workload_requested_at TIMESTAMPTZ,
                llm_state TEXT,
                llm_owner TEXT,
                llm_active BOOLEAN NOT NULL DEFAULT FALSE,
                llm_owner_heartbeat_at TIMESTAMPTZ,
                llm_active_workload TEXT,
                llm_active_request_id TEXT,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """
        )
        connection.execute("INSERT INTO gpu_runtime_coordination(id, llm_state) VALUES (1, 'STOPPED')")
        yield conninfo
        connection.execute("DROP TABLE IF EXISTS gpu_runtime_coordination")


def test_shared_owner_heartbeat_and_stale_recovery(coordination_database):
    owner_a = "summary-runtime:ci:one"
    owner_b = "summary-runtime:ci:two"
    first = GpuRuntimeCoordinator(coordination_database, owner=owner_a)
    second = GpuRuntimeCoordinator(coordination_database, owner=owner_b)

    assert first.mark_llm_resident(owner_a, "ASSISTANT", "query-a") is True
    assert first.heartbeat_owner(owner_a) is True
    assert second.mark_llm_resident(owner_b, "SUMMARY", "job-b") is False

    with psycopg.connect(coordination_database, autocommit=True) as connection:
        connection.execute(
            "UPDATE gpu_runtime_coordination SET llm_owner_heartbeat_at=now()-interval '60 seconds' WHERE id=1"
        )

    assert second.mark_llm_resident(owner_b, "SUMMARY", "job-b") is True
    second.mark_llm_stopped(owner_b)


def test_active_owner_is_not_reclaimed_while_gpu_lock_is_held(coordination_database):
    owner = "summary-runtime:ci:active"
    coordinator = GpuRuntimeCoordinator(coordination_database, owner=owner)
    assert coordinator.mark_llm_resident(owner, "ASSISTANT", "query-active") is True
    coordinator.mark_llm_busy(owner, "ASSISTANT", "query-active")

    with psycopg.connect(coordination_database, autocommit=True) as connection:
        connection.execute(
            "UPDATE gpu_runtime_coordination SET llm_owner_heartbeat_at=now()-interval '60 seconds' WHERE id=1"
        )
        assert connection.execute(
            "SELECT pg_try_advisory_lock(hashtextextended(%s, 0))", ("whisperx-atom-gpu-0",)
        ).fetchone()[0] is True
        assert coordinator.reclaim_stale_owner() is False
        connection.execute(
            "SELECT pg_advisory_unlock(hashtextextended(%s, 0))", ("whisperx-atom-gpu-0",)
        )

    assert coordinator.reclaim_stale_owner() is True
