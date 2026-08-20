-- Bound retries for transient Assistant infrastructure failures.
-- The columns are additive so older API/worker binaries continue to work;
-- only the current Assistant worker writes the retry metadata.
ALTER TABLE assistant_queries
  ADD COLUMN IF NOT EXISTS retry_count integer NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS next_retry_at timestamptz,
  ADD COLUMN IF NOT EXISTS retryable boolean NOT NULL DEFAULT true;

ALTER TABLE assistant_queries
  DROP CONSTRAINT IF EXISTS assistant_queries_retry_count_check;

ALTER TABLE assistant_queries
  ADD CONSTRAINT assistant_queries_retry_count_check CHECK (retry_count >= 0);

CREATE INDEX IF NOT EXISTS ix_assistant_queries_retry_due
  ON assistant_queries(status, next_retry_at)
  WHERE status = 'QUEUED' AND next_retry_at IS NOT NULL;
