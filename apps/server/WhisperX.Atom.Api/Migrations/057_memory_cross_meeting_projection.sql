-- Cross-meeting Memory v2 projection support.
-- This is an additive derived-index migration. Canonical transcripts and
-- transcript segments are never rewritten by this migration.

ALTER TABLE transcript_facts
    ADD COLUMN IF NOT EXISTS subject_normalized text;

CREATE INDEX IF NOT EXISTS ix_transcript_facts_owner_active_subject
    ON transcript_facts(owner_user_id, state, subject_normalized, fact_type, created_at DESC)
    WHERE state='ACTIVE' AND subject_normalized IS NOT NULL;

COMMENT ON COLUMN transcript_facts.subject_normalized IS
    'Versioned deterministic topic key used only for owner-scoped Memory projection; NULL means no unambiguous subject.';
