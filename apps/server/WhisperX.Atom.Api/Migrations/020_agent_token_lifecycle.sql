ALTER TABLE recorder_agents ADD COLUMN IF NOT EXISTS token_revoked_at timestamptz;
CREATE INDEX IF NOT EXISTS ix_recorder_agents_active_token
  ON recorder_agents(id) WHERE token_revoked_at IS NULL;
