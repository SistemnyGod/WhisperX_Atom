-- Russian-language retrieval for Assistant.  This is additive: the legacy
-- simple-config index remains intact for existing search clients.
CREATE INDEX IF NOT EXISTS ix_transcript_segments_text_russian
  ON transcript_segments
  USING gin (to_tsvector('russian', coalesce(text, '')));

CREATE INDEX IF NOT EXISTS ix_assistant_queries_meeting_status
  ON assistant_queries(meeting_id, status, created_at DESC);
