ALTER TABLE recording_sessions
    ADD COLUMN IF NOT EXISTS acoustic_profile text NOT NULL DEFAULT 'AUTO';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'recording_sessions_acoustic_profile_check'
    ) THEN
        ALTER TABLE recording_sessions
            ADD CONSTRAINT recording_sessions_acoustic_profile_check
            CHECK (acoustic_profile IN ('AUTO', 'STANDARD', 'LARGE_ROOM'));
    END IF;
END $$;
