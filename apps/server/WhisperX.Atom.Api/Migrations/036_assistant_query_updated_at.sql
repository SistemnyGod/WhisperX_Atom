-- Keep assistant query lease/heartbeat updates compatible with the original
-- assistant_queries table, which predates the worker's updated_at touch.
-- Additive and idempotent: existing query rows and statuses are preserved.
ALTER TABLE assistant_queries
  ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

UPDATE assistant_queries
SET updated_at = COALESCE(updated_at, created_at, now())
WHERE updated_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_assistant_queries_updated_at
  ON assistant_queries(updated_at DESC);
