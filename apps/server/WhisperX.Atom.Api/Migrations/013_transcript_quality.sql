ALTER TABLE transcripts ADD COLUMN IF NOT EXISTS warnings jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE transcripts ADD COLUMN IF NOT EXISTS quality_metadata jsonb NOT NULL DEFAULT '{}'::jsonb;
