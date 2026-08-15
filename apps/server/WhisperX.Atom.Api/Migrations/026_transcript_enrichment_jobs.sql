-- Transcript V1 (ASR_DRAFT) is durable before alignment/diarization. An
-- optional enrichment job may produce V2 without mutating the V1 row.
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS input_transcript_id uuid REFERENCES transcripts(id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_active_transcript_enrichment
  ON jobs(input_transcript_id)
  WHERE type='TRANSCRIPT_ENRICH' AND status IN ('QUEUED','RUNNING');

