-- Historical transcript imports are receipts over local source documents.
-- The source path and transcript text never enter this table.
CREATE TABLE IF NOT EXISTS historical_transcript_imports(
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    source_name text NOT NULL,
    source_format text NOT NULL,
    source_sha256 text NOT NULL,
    canonical_content_sha256 text NOT NULL,
    parser_version text NOT NULL,
    decision text NOT NULL,
    meeting_id uuid REFERENCES meetings(id) ON DELETE SET NULL,
    transcript_id uuid REFERENCES transcripts(id) ON DELETE SET NULL,
    memory_job_id uuid REFERENCES memory_jobs(id) ON DELETE SET NULL,
    receipt jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(owner_user_id, canonical_content_sha256)
);

CREATE INDEX IF NOT EXISTS ix_historical_imports_owner_created
    ON historical_transcript_imports(owner_user_id, created_at DESC);

COMMENT ON TABLE historical_transcript_imports IS
    'Idempotent, privacy-safe receipt for historical transcript imports; source paths and raw text are intentionally excluded.';

