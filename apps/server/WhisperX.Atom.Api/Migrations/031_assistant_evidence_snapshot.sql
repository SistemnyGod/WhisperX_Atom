-- Preserve both the prompt retrieval set and citations chosen by Qwen.
-- Existing rows remain RETRIEVED for backward compatibility.
ALTER TABLE assistant_query_evidence
  ADD COLUMN IF NOT EXISTS snapshot_kind text NOT NULL DEFAULT 'RETRIEVED',
  ADD COLUMN IF NOT EXISTS meeting_id uuid,
  ADD COLUMN IF NOT EXISTS start_ms bigint,
  ADD COLUMN IF NOT EXISTS end_ms bigint,
  ADD COLUMN IF NOT EXISTS text_sha256 text;

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'assistant_query_evidence_pkey') THEN
    ALTER TABLE assistant_query_evidence DROP CONSTRAINT assistant_query_evidence_pkey;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'assistant_query_evidence_snapshot_kind_check') THEN
    ALTER TABLE assistant_query_evidence ADD CONSTRAINT assistant_query_evidence_snapshot_kind_check
      CHECK (snapshot_kind IN ('RETRIEVED','CITED'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'assistant_query_evidence_pkey') THEN
    ALTER TABLE assistant_query_evidence ADD CONSTRAINT assistant_query_evidence_pkey
      PRIMARY KEY(query_id, segment_id, snapshot_kind);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_assistant_query_evidence_snapshot
  ON assistant_query_evidence(query_id, snapshot_kind, rank);
