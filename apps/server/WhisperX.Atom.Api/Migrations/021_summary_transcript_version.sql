ALTER TABLE jobs ADD COLUMN IF NOT EXISTS input_transcript_id uuid REFERENCES transcripts(id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_active_summary_per_transcript
  ON jobs(input_transcript_id)
  WHERE type='SUMMARIZE' AND status IN ('QUEUED','RUNNING');
