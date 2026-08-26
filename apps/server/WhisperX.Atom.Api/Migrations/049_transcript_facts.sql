-- Derived transcript facts for Intelligence v2.
-- Facts are an index over immutable transcript evidence, never a replacement
-- for transcript_segments. Every active fact carries transcript version and
-- segment IDs so a newer V2 can invalidate/rebuild the derived rows.
CREATE TABLE IF NOT EXISTS transcript_facts(
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    meeting_id uuid NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
    transcript_id uuid NOT NULL REFERENCES transcripts(id) ON DELETE CASCADE,
    transcript_version integer NOT NULL,
    fact_type text NOT NULL,
    subject text,
    predicate text,
    value text,
    speaker_id uuid REFERENCES meeting_speakers(id) ON DELETE SET NULL,
    start_ms bigint,
    end_ms bigint,
    confidence numeric(5,4),
    evidence_segment_ids jsonb NOT NULL DEFAULT '[]'::jsonb,
    state text NOT NULL DEFAULT 'ACTIVE',
    created_at timestamptz NOT NULL DEFAULT now(),
    invalidated_at timestamptz
);

CREATE INDEX IF NOT EXISTS ix_transcript_facts_meeting_type
    ON transcript_facts(meeting_id, fact_type, state, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_transcript_facts_transcript_version
    ON transcript_facts(transcript_id, transcript_version, state);

CREATE OR REPLACE FUNCTION invalidate_transcript_facts_for_new_version()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.version > 1 THEN
        UPDATE transcript_facts
           SET state='INVALIDATED', invalidated_at=COALESCE(invalidated_at, now())
         WHERE meeting_id=NEW.meeting_id
           AND transcript_version < NEW.version
           AND state='ACTIVE';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_transcript_facts_invalidate_on_version ON transcripts;
CREATE TRIGGER trg_transcript_facts_invalidate_on_version
AFTER INSERT OR UPDATE OF version ON transcripts
FOR EACH ROW EXECUTE FUNCTION invalidate_transcript_facts_for_new_version();
