-- First-class meeting occurrence time for calendar-scoped Assistant retrieval.
-- This is additive: canonical transcripts, segments and existing summaries
-- are never rewritten or deleted.
ALTER TABLE meetings
    ADD COLUMN IF NOT EXISTS occurred_at timestamptz,
    ADD COLUMN IF NOT EXISTS occurred_precision text NOT NULL DEFAULT 'UNKNOWN';

-- Historical imports carry their reviewed meeting date in the privacy-safe
-- receipt. Prefer that date before capture/created_at; the latter are only
-- fallbacks for meetings without an explicit occurrence.
UPDATE meetings m
   SET occurred_at = CASE
           WHEN h.receipt->>'meetingDate' ~ '^\d{4}-\d{2}-\d{2}$'
               THEN ((h.receipt->>'meetingDate')::date)::timestamptz
           WHEN h.receipt->>'meetingDate' ~ '^\d{4}-\d{2}-\d{2}[T ]'
               THEN (h.receipt->>'meetingDate')::timestamptz
           ELSE m.occurred_at
       END,
       occurred_precision = CASE
           WHEN h.receipt->>'meetingDate' ~ '^\d{4}-\d{2}-\d{2}$' THEN 'IMPORTED_DATE'
           WHEN h.receipt->>'meetingDate' ~ '^\d{4}-\d{2}-\d{2}[T ]' THEN 'IMPORTED_TIMESTAMP'
           ELSE m.occurred_precision
       END
  FROM historical_transcript_imports h
 WHERE h.meeting_id=m.id
   AND m.occurred_at IS NULL;

UPDATE meetings m
   SET occurred_at = COALESCE(
       (SELECT MIN(rs.started_at) FROM recording_sessions rs WHERE rs.meeting_id=m.id),
       m.created_at
   ),
       occurred_precision = CASE
           WHEN EXISTS (SELECT 1 FROM recording_sessions rs WHERE rs.meeting_id=m.id AND rs.started_at IS NOT NULL) THEN 'CAPTURED'
           ELSE 'CREATED_AT'
       END
 WHERE m.occurred_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_meetings_occurred_at
    ON meetings(occurred_at DESC, id);

COMMENT ON COLUMN meetings.occurred_at IS
    'Occurrence timestamp used for user-local temporal retrieval; not a replacement for created_at.';
COMMENT ON COLUMN meetings.occurred_precision IS
    'CAPTURED, IMPORTED_DATE, CREATED_AT or UNKNOWN.';
