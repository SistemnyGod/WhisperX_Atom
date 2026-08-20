-- Track provenance for provisional LIVE_MEETING evidence. This is additive:
-- existing rows remain valid and are treated as room microphone segments.
ALTER TABLE live_meeting_segments
  ADD COLUMN IF NOT EXISTS source_track_type text NOT NULL DEFAULT 'room-microphone',
  ADD COLUMN IF NOT EXISTS source_track_id text,
  ADD COLUMN IF NOT EXISTS channel_role text NOT NULL DEFAULT 'LOCAL_ROOM',
  ADD COLUMN IF NOT EXISTS quality_flags text;

ALTER TABLE live_meeting_segments
  DROP CONSTRAINT IF EXISTS live_meeting_segments_source_track_type_check;
ALTER TABLE live_meeting_segments
  ADD CONSTRAINT live_meeting_segments_source_track_type_check
  CHECK (source_track_type IN ('room-microphone','system-audio'));

ALTER TABLE live_meeting_segments
  DROP CONSTRAINT IF EXISTS live_meeting_segments_channel_role_check;
ALTER TABLE live_meeting_segments
  ADD CONSTRAINT live_meeting_segments_channel_role_check
  CHECK (channel_role IN ('LOCAL_ROOM','REMOTE_SYSTEM','MIC_FALLBACK'));

ALTER TABLE assistant_live_query_evidence
  ADD COLUMN IF NOT EXISTS source_track_type text,
  ADD COLUMN IF NOT EXISTS source_track_id text,
  ADD COLUMN IF NOT EXISTS channel_role text;

ALTER TABLE assistant_live_query_evidence
  DROP CONSTRAINT IF EXISTS assistant_live_query_evidence_source_track_type_check;
ALTER TABLE assistant_live_query_evidence
  ADD CONSTRAINT assistant_live_query_evidence_source_track_type_check
  CHECK (source_track_type IS NULL OR source_track_type IN ('room-microphone','system-audio'));

ALTER TABLE assistant_live_query_evidence
  DROP CONSTRAINT IF EXISTS assistant_live_query_evidence_channel_role_check;
ALTER TABLE assistant_live_query_evidence
  ADD CONSTRAINT assistant_live_query_evidence_channel_role_check
  CHECK (channel_role IS NULL OR channel_role IN ('LOCAL_ROOM','REMOTE_SYSTEM','MIC_FALLBACK'));

UPDATE live_meeting_segments
SET source_track_type = COALESCE(NULLIF(source_track_type, ''), 'room-microphone'),
    channel_role = COALESCE(NULLIF(channel_role, ''), 'LOCAL_ROOM')
WHERE source_track_type IS NULL OR channel_role IS NULL;

CREATE INDEX IF NOT EXISTS ix_live_meeting_segments_source
  ON live_meeting_segments(meeting_id, recording_session_id, source_track_type, start_ms, id);
