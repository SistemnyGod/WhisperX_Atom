"""Sequential PostgreSQL tests for the production Memory projection path.

These tests intentionally require an explicit WHISPERX_TEST_DATABASE. They
never use DATABASE_URL, so a normal test run cannot touch a developer or LAN
runtime database.
"""

from __future__ import annotations

import os
import uuid

import pytest

psycopg = pytest.importorskip("psycopg")
from workers.memory_worker.worker import MemoryProjectionRepository


pytestmark = pytest.mark.integration


@pytest.fixture()
def memory_database():
    conninfo = os.getenv("WHISPERX_TEST_DATABASE")
    if not conninfo:
        pytest.skip("set WHISPERX_TEST_DATABASE to run PostgreSQL Memory integration tests")
    with psycopg.connect(conninfo, autocommit=True) as connection:
        if not connection.execute("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version='057_memory_cross_meeting_projection')").fetchone()[0]:
            pytest.skip("test database has not applied migration 057")
    yield conninfo


def _setup_chain(connection):
    owner = uuid.uuid4()
    user_name = f"memory-test-{owner}"
    meeting_a, meeting_b = uuid.uuid4(), uuid.uuid4()
    transcript_a, transcript_b = uuid.uuid4(), uuid.uuid4()
    fact_a, fact_b = uuid.uuid4(), uuid.uuid4()
    job_a, job_b = uuid.uuid4(), uuid.uuid4()
    connection.execute(
        "INSERT INTO users(id,username,password_hash,role) VALUES(%s,%s,'test','USER')",
        (owner, user_name),
    )
    connection.execute(
        """INSERT INTO meetings(id,title,description,status,owner_id,started_at)
           VALUES(%s,'Memory A','', 'READY',%s,%s),(%s,'Memory B','', 'READY',%s,%s)""",
        (meeting_a, owner, "2026-08-01T10:00:00Z", meeting_b, owner, "2026-08-02T10:00:00Z"),
    )
    connection.execute(
        "INSERT INTO transcripts(id,meeting_id,version,status) VALUES(%s,%s,2,'READY'),(%s,%s,2,'READY')",
        (transcript_a, meeting_a, transcript_b, meeting_b),
    )
    subject_a = "ремонт печь №2"
    subject_b = "ремонт печь №2"
    connection.execute(
        """INSERT INTO transcript_facts(
             id,owner_user_id,meeting_id,transcript_id,transcript_version,fact_type,subject,subject_normalized,
             value,start_ms,end_ms,confidence,evidence_segment_ids,state,source_text)
           VALUES(%s,%s,%s,%s,2,'DEADLINE','ремонт второй печи',%s,'25 августа',100,200,.9,'[\"%s\"]','ACTIVE','Срок 25 августа.'),
                 (%s,%s,%s,%s,2,'DEADLINE','ремонта печи №2',%s,'30 августа',100,200,.9,'[\"%s\"]','ACTIVE','Срок теперь 30 августа.')""",
        (fact_a, owner, meeting_a, transcript_a, subject_a, fact_a, fact_b, owner, meeting_b, transcript_b, subject_b, fact_b),
    )
    connection.execute(
        """INSERT INTO memory_jobs(id,owner_user_id,meeting_id,transcript_id,transcript_version,status,stage,progress,attempt)
           VALUES(%s,%s,%s,%s,2,'QUEUED','QUEUED',0,0),(%s,%s,%s,%s,2,'QUEUED','QUEUED',0,0)""",
        (job_a, owner, meeting_a, transcript_a, job_b, owner, meeting_b, transcript_b),
    )
    return owner, user_name, meeting_a, meeting_b, transcript_a, transcript_b, fact_a, fact_b, job_a, job_b


def _payload(job_id, meeting_id, transcript_id, owner):
    return {
        "jobId": str(job_id),
        "meetingId": str(meeting_id),
        "transcriptId": str(transcript_id),
        "transcriptVersion": 2,
        "ownerUserId": str(owner),
    }


def test_sequential_jobs_keep_cross_meeting_thread_and_relations(memory_database):
    values = None
    with psycopg.connect(memory_database, autocommit=True) as connection:
        values = _setup_chain(connection)
        owner, user_name, meeting_a, meeting_b, transcript_a, transcript_b, fact_a, fact_b, job_a, job_b = values
    try:
        repository = MemoryProjectionRepository(memory_database)
        repository.worker_id = "memory-integration-worker"
        assert repository.claim(str(job_a)) == "ACQUIRED"
        assert repository.process(_payload(job_a, meeting_a, transcript_a, owner)).status == "READY"
        assert repository.claim(str(job_b)) == "ACQUIRED"
        assert repository.process(_payload(job_b, meeting_b, transcript_b, owner)).status == "READY"
        with psycopg.connect(memory_database, autocommit=True) as connection:
            thread = connection.execute("SELECT id FROM memory_threads WHERE owner_user_id=%s AND normalized_title='ремонт печь №2'", (owner,)).fetchone()
            assert thread is not None
            assert connection.execute("SELECT count(*) FROM memory_thread_facts WHERE thread_id=%s", (thread[0],)).fetchone()[0] == 2
            assert connection.execute(
                "SELECT relation_type FROM memory_fact_relations WHERE source_fact_id=%s AND target_fact_id=%s AND invalidated_at IS NULL",
                (fact_a, fact_b),
            ).fetchone()[0] == "SUPERSEDES"
    finally:
        with psycopg.connect(memory_database, autocommit=True) as connection:
            connection.execute("DELETE FROM memory_jobs WHERE owner_user_id=%s", (owner,))
            connection.execute("DELETE FROM transcript_facts WHERE owner_user_id=%s", (owner,))
            connection.execute("DELETE FROM transcripts WHERE meeting_id IN (%s,%s)", (meeting_a, meeting_b))
            connection.execute("DELETE FROM meetings WHERE owner_id=%s", (owner,))
            connection.execute("DELETE FROM users WHERE id=%s", (owner,))
