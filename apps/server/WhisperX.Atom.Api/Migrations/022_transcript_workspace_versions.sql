ALTER TABLE transcripts ADD COLUMN IF NOT EXISTS source_transcript_id uuid REFERENCES transcripts(id);
ALTER TABLE transcripts ADD COLUMN IF NOT EXISTS version_kind text NOT NULL DEFAULT 'GENERATED';
ALTER TABLE transcripts ADD COLUMN IF NOT EXISTS edited_by_user_id uuid REFERENCES users(id);
ALTER TABLE transcripts ADD COLUMN IF NOT EXISTS edit_reason text;

CREATE INDEX IF NOT EXISTS ix_transcripts_meeting_version_desc
  ON transcripts(meeting_id, version DESC);
