-- Bound repeated QUEUED watchdog emissions without changing job identity or
-- the durable outbox contract.  Existing rows start at zero and remain
-- immediately eligible under the original watchdog interval.
ALTER TABLE jobs
  ADD COLUMN IF NOT EXISTS watchdog_requeue_count integer NOT NULL DEFAULT 0;
ALTER TABLE jobs
  ADD COLUMN IF NOT EXISTS last_watchdog_requeue_at timestamptz;

CREATE INDEX IF NOT EXISTS ix_jobs_queued_watchdog
  ON jobs(status, updated_at)
  WHERE status='QUEUED';
