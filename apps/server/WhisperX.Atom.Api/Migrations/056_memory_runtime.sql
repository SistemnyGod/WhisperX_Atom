-- Durable CPU-only Meeting Memory indexing runtime.
-- This migration is additive: canonical transcripts, segments and V1/V2 jobs
-- are never rewritten or deleted by the memory projection.

ALTER TABLE memory_jobs
    ADD COLUMN IF NOT EXISTS worker_id text,
    ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz,
    ADD COLUMN IF NOT EXISTS last_heartbeat timestamptz,
    ADD COLUMN IF NOT EXISTS next_retry_at timestamptz,
    ADD COLUMN IF NOT EXISTS error_message text,
    ADD COLUMN IF NOT EXISTS max_attempts integer NOT NULL DEFAULT 3,
    ADD COLUMN IF NOT EXISTS started_stage_at timestamptz;

ALTER TABLE transcript_facts
    ADD COLUMN IF NOT EXISTS source_text text;

CREATE INDEX IF NOT EXISTS ix_memory_jobs_dispatch
    ON memory_jobs(status, next_retry_at, created_at, id)
    WHERE status IN ('QUEUED', 'RUNNING');

CREATE INDEX IF NOT EXISTS ix_memory_jobs_owner_transcript
    ON memory_jobs(owner_user_id, transcript_id, transcript_version, status);

CREATE INDEX IF NOT EXISTS ix_memory_facts_owner_subject
    ON transcript_facts(owner_user_id, state, fact_type, subject, created_at DESC);

CREATE OR REPLACE FUNCTION invalidate_memory_projection_for_fact()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.state = 'INVALIDATED' AND OLD.state <> 'INVALIDATED' THEN
        UPDATE memory_fact_relations
           SET invalidated_at = COALESCE(invalidated_at, now())
         WHERE invalidated_at IS NULL
           AND (source_fact_id = NEW.id OR target_fact_id = NEW.id);
        DELETE FROM memory_thread_facts WHERE fact_id = NEW.id;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_memory_projection_fact_invalidated ON transcript_facts;
CREATE TRIGGER trg_memory_projection_fact_invalidated
AFTER UPDATE OF state ON transcript_facts
FOR EACH ROW EXECUTE FUNCTION invalidate_memory_projection_for_fact();
