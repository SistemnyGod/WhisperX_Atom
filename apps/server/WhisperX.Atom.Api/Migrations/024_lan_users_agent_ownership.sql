ALTER TABLE users ADD COLUMN IF NOT EXISTS must_change_password boolean NOT NULL DEFAULT false;
ALTER TABLE users ADD COLUMN IF NOT EXISTS password_changed_at timestamptz;

CREATE TABLE IF NOT EXISTS agent_user_links(
  agent_id uuid NOT NULL REFERENCES recorder_agents(id) ON DELETE CASCADE,
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  is_active boolean NOT NULL DEFAULT true,
  last_used_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(agent_id, user_id)
);
CREATE INDEX IF NOT EXISTS ix_agent_user_links_user ON agent_user_links(user_id, is_active);

ALTER TABLE recording_sessions ADD COLUMN IF NOT EXISTS owner_user_id uuid REFERENCES users(id);
UPDATE recording_sessions rs
SET owner_user_id = m.owner_id
FROM meetings m
WHERE m.id = rs.meeting_id
  AND rs.owner_user_id IS NULL
  AND m.owner_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_recording_sessions_owner ON recording_sessions(owner_user_id, created_at DESC);

