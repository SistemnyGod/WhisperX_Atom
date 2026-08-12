ALTER TABLE recording_sessions ADD COLUMN IF NOT EXISTS pipeline_correlation_id text;
ALTER TABLE recording_sessions ADD COLUMN IF NOT EXISTS local_session_id text;
ALTER TABLE recording_sessions ADD COLUMN IF NOT EXISTS stage_timings jsonb NOT NULL DEFAULT '{}'::jsonb;
CREATE UNIQUE INDEX IF NOT EXISTS ux_recording_sessions_pipeline_correlation
  ON recording_sessions(pipeline_correlation_id) WHERE pipeline_correlation_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_recording_sessions_meeting_correlation
  ON recording_sessions(meeting_id, pipeline_correlation_id);
CREATE INDEX IF NOT EXISTS ix_recording_sessions_local_session ON recording_sessions(local_session_id);
