-- Durable hand-off between GPU ASR and the resident Qwen runtime.
-- This is additive and contains no user/media data.
CREATE TABLE IF NOT EXISTS gpu_runtime_coordination (
    id SMALLINT PRIMARY KEY CHECK (id = 1),
    asr_state TEXT NOT NULL DEFAULT 'IDLE'
        CHECK (asr_state IN ('IDLE', 'ASR_PENDING')),
    asr_request_id TEXT,
    asr_job_id UUID,
    asr_owner TEXT,
    asr_requested_at TIMESTAMPTZ,
    llm_state TEXT NOT NULL DEFAULT 'STOPPED'
        CHECK (llm_state IN ('STOPPED', 'STARTING', 'RESIDENT', 'STOPPING')),
    llm_owner TEXT,
    llm_ack_request_id TEXT,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

INSERT INTO gpu_runtime_coordination (id)
VALUES (1)
ON CONFLICT (id) DO NOTHING;

CREATE INDEX IF NOT EXISTS ix_gpu_runtime_coordination_asr
    ON gpu_runtime_coordination (asr_request_id)
    WHERE asr_request_id IS NOT NULL;

-- The assistant query uses this exact expression with @@, allowing PostgreSQL
-- to use a GIN index before the bounded semantic pool is materialized.
CREATE INDEX IF NOT EXISTS ix_transcript_segments_russian_fts
    ON transcript_segments USING GIN (to_tsvector('russian', COALESCE(text, '')));
