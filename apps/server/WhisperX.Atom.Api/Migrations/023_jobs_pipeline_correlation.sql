ALTER TABLE jobs ADD COLUMN IF NOT EXISTS pipeline_correlation_id text;

UPDATE jobs j
SET pipeline_correlation_id = rs.pipeline_correlation_id
FROM recording_sessions rs
WHERE rs.meeting_id = j.meeting_id
  AND rs.pipeline_correlation_id IS NOT NULL
  AND j.pipeline_correlation_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_jobs_pipeline_correlation
  ON jobs(pipeline_correlation_id)
  WHERE pipeline_correlation_id IS NOT NULL;
