-- Unified Assistant request metadata and immutable grounding evidence.
-- This migration is additive and keeps the existing assistant API compatible.
ALTER TABLE assistant_queries
  ADD COLUMN IF NOT EXISTS requested_mode text,
  ADD COLUMN IF NOT EXISTS router_confidence double precision,
  ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT 'DESKTOP',
  ADD COLUMN IF NOT EXISTS grounding_status text NOT NULL DEFAULT 'PENDING',
  ADD COLUMN IF NOT EXISTS answer_metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
  ADD COLUMN IF NOT EXISTS transcript_id uuid REFERENCES transcripts(id),
  ADD COLUMN IF NOT EXISTS transcript_version integer;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'assistant_queries_requested_mode_check'
  ) THEN
    ALTER TABLE assistant_queries ADD CONSTRAINT assistant_queries_requested_mode_check
      CHECK (requested_mode IS NULL OR requested_mode IN ('AUTO','GENERAL_CHAT','MEETING_MEMORY','MEETING_HISTORY','CURRENT_MEETING'));
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'assistant_queries_source_check'
  ) THEN
    ALTER TABLE assistant_queries ADD CONSTRAINT assistant_queries_source_check
      CHECK (source IN ('DESKTOP','VOICE','SYSTEM'));
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'assistant_queries_grounding_status_check'
  ) THEN
    ALTER TABLE assistant_queries ADD CONSTRAINT assistant_queries_grounding_status_check
      CHECK (grounding_status IN ('PENDING','GROUNDED','WARNING','NO_EVIDENCE','REJECTED'));
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS assistant_query_evidence(
  query_id uuid NOT NULL REFERENCES assistant_queries(id) ON DELETE CASCADE,
  segment_id uuid NOT NULL REFERENCES transcript_segments(id),
  rank integer NOT NULL CHECK (rank > 0),
  retrieval_score double precision,
  transcript_id uuid REFERENCES transcripts(id),
  transcript_version integer,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(query_id, segment_id)
);

CREATE INDEX IF NOT EXISTS ix_assistant_query_evidence_query_rank
  ON assistant_query_evidence(query_id, rank);
CREATE INDEX IF NOT EXISTS ix_assistant_queries_source_status
  ON assistant_queries(source, status, created_at);
CREATE INDEX IF NOT EXISTS ix_assistant_queries_grounding
  ON assistant_queries(grounding_status, created_at);
