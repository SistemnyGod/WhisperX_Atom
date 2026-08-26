ALTER TABLE inbox_messages ADD COLUMN IF NOT EXISTS job_id uuid;
ALTER TABLE inbox_messages ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz;
ALTER TABLE inbox_messages ADD COLUMN IF NOT EXISTS worker_id text;
UPDATE inbox_messages SET lease_expires_at = now() WHERE lease_expires_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_inbox_messages_lease ON inbox_messages(lease_expires_at);
