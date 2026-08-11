ALTER TABLE summaries ADD COLUMN IF NOT EXISTS job_id uuid REFERENCES jobs(id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_summaries_job_id
  ON summaries(job_id)
  WHERE job_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS audit_events(
  id uuid PRIMARY KEY,
  actor_user_id uuid REFERENCES users(id),
  meeting_id uuid REFERENCES meetings(id),
  entity_type text NOT NULL,
  entity_id uuid,
  event_type text NOT NULL,
  before_state jsonb,
  after_state jsonb,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_audit_events_meeting_created
  ON audit_events(meeting_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_audit_events_entity_created
  ON audit_events(entity_type, entity_id, created_at DESC);
