-- Memory indexing is background enrichment. It must never gate V1/V2/Summary.
CREATE TABLE IF NOT EXISTS memory_jobs(
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    meeting_id uuid NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
    transcript_id uuid NOT NULL REFERENCES transcripts(id) ON DELETE CASCADE,
    transcript_version integer NOT NULL,
    status text NOT NULL DEFAULT 'QUEUED',
    stage text NOT NULL DEFAULT 'QUEUED',
    progress integer NOT NULL DEFAULT 0,
    attempt integer NOT NULL DEFAULT 0,
    error_code text,
    pipeline_correlation_id uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    started_at timestamptz,
    completed_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(transcript_id, transcript_version)
);

CREATE INDEX IF NOT EXISTS ix_memory_jobs_owner_status
    ON memory_jobs(owner_user_id, status, updated_at DESC);
