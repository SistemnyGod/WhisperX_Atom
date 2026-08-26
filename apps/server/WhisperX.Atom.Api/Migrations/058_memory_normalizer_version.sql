-- Persist the deterministic subject-key algorithm used by the derived Memory
-- projection.  Existing facts are explicitly tagged with v1; future key
-- changes can be detected and reindexed instead of silently mixing keys.
ALTER TABLE transcript_facts
    ADD COLUMN IF NOT EXISTS subject_normalizer_version integer NOT NULL DEFAULT 1;

CREATE INDEX IF NOT EXISTS ix_transcript_facts_memory_normalizer
    ON transcript_facts(owner_user_id, subject_normalizer_version, state, subject_normalized)
    WHERE state='ACTIVE' AND subject_normalized IS NOT NULL;

COMMENT ON COLUMN transcript_facts.subject_normalizer_version IS
    'Deterministic subject normalizer version used for subject_normalized; mismatches require Memory reindex.';
