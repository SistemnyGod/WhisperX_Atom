-- Ephemeral provisional ASR memory for questions during an active recording.
-- It deliberately contains text/timing metadata only: no audio and no
-- canonical transcript rows are written here. Rows expire automatically via
-- expires_at and are never used by final V1/V2 retrieval.
CREATE TABLE IF NOT EXISTS live_meeting_segments(
  id uuid PRIMARY KEY,
  meeting_id uuid NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
  recording_session_id uuid REFERENCES recording_sessions(id) ON DELETE CASCADE,
  start_ms bigint NOT NULL CHECK (start_ms >= 0),
  end_ms bigint NOT NULL CHECK (end_ms > start_ms),
  text text NOT NULL CHECK (length(text) BETWEEN 1 AND 2000),
  confidence double precision CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
  revision integer NOT NULL DEFAULT 0 CHECK (revision >= 0),
  captured_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  created_by uuid REFERENCES users(id),
  CONSTRAINT live_meeting_segments_expiry_check CHECK (expires_at > captured_at)
);
CREATE INDEX IF NOT EXISTS ix_live_meeting_segments_scope
  ON live_meeting_segments(meeting_id, recording_session_id, start_ms, id);
CREATE INDEX IF NOT EXISTS ix_live_meeting_segments_expiry
  ON live_meeting_segments(expires_at);

-- Live evidence has no transcript_segments FK by design. Keeping it in a
-- separate snapshot table preserves the immutable evidence contract without
-- polluting canonical transcript history.
CREATE TABLE IF NOT EXISTS assistant_live_query_evidence(
  query_id uuid NOT NULL REFERENCES assistant_queries(id) ON DELETE CASCADE,
  live_segment_id uuid NOT NULL REFERENCES live_meeting_segments(id) ON DELETE CASCADE,
  rank integer NOT NULL CHECK (rank > 0),
  snapshot_kind text NOT NULL CHECK (snapshot_kind IN ('RETRIEVED','CITED')),
  meeting_id uuid NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
  start_ms bigint NOT NULL,
  end_ms bigint NOT NULL,
  text_sha256 text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(query_id, live_segment_id, snapshot_kind)
);
CREATE INDEX IF NOT EXISTS ix_assistant_live_query_evidence_snapshot
  ON assistant_live_query_evidence(query_id, snapshot_kind, rank);

-- LIVE_MEETING is a meeting-scoped mode. Keep old values and constraints
-- intact for rolling upgrades, while allowing the new mode to be introduced
-- before Desktop starts sending it.
ALTER TABLE assistant_conversations DROP CONSTRAINT IF EXISTS assistant_conversations_scope_mode_check;
ALTER TABLE assistant_conversations DROP CONSTRAINT IF EXISTS assistant_conversations_scope_meeting_check;
ALTER TABLE assistant_queries DROP CONSTRAINT IF EXISTS assistant_queries_mode_check;
ALTER TABLE assistant_queries DROP CONSTRAINT IF EXISTS assistant_queries_requested_mode_check;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='assistant_conversations_scope_mode_check') THEN
    ALTER TABLE assistant_conversations ADD CONSTRAINT assistant_conversations_scope_mode_check
      CHECK (assistant_mode IN ('GENERAL_CHAT','MEETING_MEMORY','CURRENT_MEETING','LIVE_MEETING'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='assistant_conversations_scope_meeting_check') THEN
    ALTER TABLE assistant_conversations ADD CONSTRAINT assistant_conversations_scope_meeting_check
      CHECK (
        (scope_type = 'GENERAL' AND meeting_id IS NULL AND assistant_mode = 'GENERAL_CHAT')
        OR (scope_type = 'MEETING' AND meeting_id IS NOT NULL AND assistant_mode IN ('CURRENT_MEETING','MEETING_MEMORY','LIVE_MEETING'))
        OR (scope_type = 'GLOBAL' AND meeting_id IS NULL AND assistant_mode = 'MEETING_MEMORY')
      );
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='assistant_queries_mode_check') THEN
    ALTER TABLE assistant_queries ADD CONSTRAINT assistant_queries_mode_check
      CHECK (assistant_mode IN ('GENERAL_CHAT','MEETING_MEMORY','CURRENT_MEETING','LIVE_MEETING'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='assistant_queries_requested_mode_check') THEN
    ALTER TABLE assistant_queries ADD CONSTRAINT assistant_queries_requested_mode_check
      CHECK (requested_mode IS NULL OR requested_mode IN ('AUTO','GENERAL_CHAT','MEETING_MEMORY','MEETING_HISTORY','CURRENT_MEETING','LIVE_MEETING'));
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_assistant_queries_live_status
  ON assistant_queries(assistant_mode, meeting_id, status, created_at)
  WHERE assistant_mode = 'LIVE_MEETING';
