-- Durable post-assembly delay for heavy GPU processing.  The worker never
-- sleeps: outbox publication remains deferred until jobs.not_before.
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS not_before timestamptz;

CREATE INDEX IF NOT EXISTS ix_jobs_not_before_queue
  ON jobs(status, not_before, created_at)
  WHERE status = 'QUEUED' AND not_before IS NOT NULL;
