ALTER TABLE transcript_segments ADD COLUMN IF NOT EXISTS segment_kind text NOT NULL DEFAULT 'SPEECH';
ALTER TABLE transcript_segments ADD COLUMN IF NOT EXISTS is_hidden boolean NOT NULL DEFAULT false;

ALTER TABLE assistant_queries ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'READY';
ALTER TABLE assistant_queries ADD COLUMN IF NOT EXISTS voice_answer text;
ALTER TABLE assistant_queries ADD COLUMN IF NOT EXISTS error_code text;
ALTER TABLE assistant_queries ADD COLUMN IF NOT EXISTS completed_at timestamptz;

CREATE INDEX IF NOT EXISTS ix_transcript_segments_visible ON transcript_segments(transcript_id, is_hidden, ordinal);
CREATE INDEX IF NOT EXISTS ix_assistant_queries_status ON assistant_queries(status, created_at);
