-- Assistant modes are additive and keep the existing conversation/query API compatible.
ALTER TABLE assistant_conversations
  ADD COLUMN IF NOT EXISTS assistant_mode text NOT NULL DEFAULT 'MEETING_MEMORY';
ALTER TABLE assistant_queries
  ADD COLUMN IF NOT EXISTS assistant_mode text NOT NULL DEFAULT 'MEETING_MEMORY';

-- Older installations created two CHECK constraints with provider-generated names.
-- Replace only the scope-related checks so this migration is safe across those names.
DO $$
DECLARE constraint_name text;
BEGIN
  FOR constraint_name IN
    SELECT conname
    FROM pg_constraint
    WHERE conrelid = 'assistant_conversations'::regclass
      AND contype = 'c'
      AND (pg_get_constraintdef(oid) ILIKE '%scope_type%'
           OR pg_get_constraintdef(oid) ILIKE '%meeting_id%')
  LOOP
    EXECUTE format('ALTER TABLE assistant_conversations DROP CONSTRAINT %I', constraint_name);
  END LOOP;
END $$;

UPDATE assistant_conversations
SET assistant_mode = CASE WHEN scope_type = 'MEETING' THEN 'CURRENT_MEETING' ELSE 'MEETING_MEMORY' END
WHERE assistant_mode IS NULL OR assistant_mode = '';

UPDATE assistant_queries
SET assistant_mode = 'MEETING_MEMORY'
WHERE assistant_mode IS NULL OR assistant_mode = '';

ALTER TABLE assistant_conversations
  ADD CONSTRAINT assistant_conversations_scope_type_check
    CHECK (scope_type IN ('MEETING','GLOBAL','GENERAL'));
ALTER TABLE assistant_conversations
  ADD CONSTRAINT assistant_conversations_scope_mode_check
    CHECK (assistant_mode IN ('GENERAL_CHAT','MEETING_MEMORY','CURRENT_MEETING'));
ALTER TABLE assistant_conversations
  ADD CONSTRAINT assistant_conversations_scope_meeting_check
    CHECK (
      (scope_type = 'GENERAL' AND meeting_id IS NULL AND assistant_mode = 'GENERAL_CHAT')
      OR (scope_type = 'MEETING' AND meeting_id IS NOT NULL AND assistant_mode IN ('CURRENT_MEETING','MEETING_MEMORY'))
      OR (scope_type = 'GLOBAL' AND meeting_id IS NULL AND assistant_mode = 'MEETING_MEMORY')
    );
ALTER TABLE assistant_queries
  ADD CONSTRAINT assistant_queries_mode_check
    CHECK (assistant_mode IN ('GENERAL_CHAT','MEETING_MEMORY','CURRENT_MEETING'));

CREATE INDEX IF NOT EXISTS ix_assistant_queries_mode_status
  ON assistant_queries(assistant_mode, status, created_at);
CREATE INDEX IF NOT EXISTS ix_assistant_conversations_mode_updated
  ON assistant_conversations(assistant_mode, updated_at DESC);
