-- Keep provisional LIVE_MEETING memory for the whole recording and the
-- post-STOP hand-off. V1 remains canonical; this is only a bounded recovery
-- context until V1 is usable (or the safety deadline is reached).
ALTER TABLE live_meeting_segments
  ADD COLUMN IF NOT EXISTS retention_policy text NOT NULL DEFAULT 'UNTIL_V1_READY';

ALTER TABLE live_meeting_segments
  DROP CONSTRAINT IF EXISTS live_meeting_segments_retention_policy_check;
ALTER TABLE live_meeting_segments
  ADD CONSTRAINT live_meeting_segments_retention_policy_check
  CHECK (retention_policy IN ('UNTIL_V1_READY','CANONICALIZED'));

-- Existing five-minute rows must not disappear while the new API is rolling
-- out. Seven days is only a safety deadline; normal cleanup happens as soon
-- as a usable V1 exists.
UPDATE live_meeting_segments
SET expires_at = GREATEST(expires_at, captured_at + interval '7 days')
WHERE retention_policy = 'UNTIL_V1_READY';

CREATE INDEX IF NOT EXISTS ix_live_meeting_segments_retention
  ON live_meeting_segments(meeting_id, retention_policy, expires_at);
