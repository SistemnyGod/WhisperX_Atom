ALTER TABLE transcripts ADD COLUMN IF NOT EXISTS quality_score numeric;
ALTER TABLE transcripts ADD COLUMN IF NOT EXISTS processing_profile text;
ALTER TABLE transcripts ADD COLUMN IF NOT EXISTS selected_asr_pass text;
