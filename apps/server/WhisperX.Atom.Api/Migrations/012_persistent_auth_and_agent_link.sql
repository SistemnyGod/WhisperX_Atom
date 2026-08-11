ALTER TABLE recorder_agents ADD COLUMN IF NOT EXISTS installation_id uuid;
CREATE UNIQUE INDEX IF NOT EXISTS ux_recorder_agents_installation_id
  ON recorder_agents(installation_id)
  WHERE installation_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS refresh_sessions(
  id uuid PRIMARY KEY,
  user_id uuid NOT NULL REFERENCES users(id),
  family_id uuid NOT NULL,
  token_hash text NOT NULL UNIQUE,
  expires_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  last_used_at timestamptz,
  revoked_at timestamptz,
  replaced_by uuid REFERENCES refresh_sessions(id)
);
CREATE INDEX IF NOT EXISTS ix_refresh_sessions_user ON refresh_sessions(user_id, expires_at DESC);
CREATE INDEX IF NOT EXISTS ix_refresh_sessions_family ON refresh_sessions(family_id);
