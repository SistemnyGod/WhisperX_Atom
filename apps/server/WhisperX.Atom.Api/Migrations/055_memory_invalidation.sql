-- Additive metadata for auditing re-indexes. Existing 049 trigger continues
-- to invalidate older versions; these fields record which version superseded it.
ALTER TABLE transcript_facts
    ADD COLUMN IF NOT EXISTS owner_user_id uuid REFERENCES users(id) ON DELETE CASCADE,
    ADD COLUMN IF NOT EXISTS value_normalized text,
    ADD COLUMN IF NOT EXISTS derivation_type text NOT NULL DEFAULT 'EXPLICIT',
    ADD COLUMN IF NOT EXISTS invalidated_by_version integer;

CREATE INDEX IF NOT EXISTS ix_transcript_facts_owner_active
    ON transcript_facts(owner_user_id, state, fact_type, created_at DESC);

CREATE OR REPLACE FUNCTION mark_transcript_facts_invalidated_by_version()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.version > 1 THEN
        UPDATE transcript_facts
           SET invalidated_by_version=NEW.version,
               invalidated_at=COALESCE(invalidated_at, now()),
               state='INVALIDATED'
         WHERE meeting_id=NEW.meeting_id
           AND transcript_version < NEW.version
           AND state='ACTIVE';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_memory_facts_invalidate_version ON transcripts;
CREATE TRIGGER trg_memory_facts_invalidate_version
AFTER INSERT OR UPDATE OF version ON transcripts
FOR EACH ROW EXECUTE FUNCTION mark_transcript_facts_invalidated_by_version();
