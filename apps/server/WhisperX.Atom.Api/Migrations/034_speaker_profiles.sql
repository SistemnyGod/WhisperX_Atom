-- Speaker Registry: explicit, owner-scoped voice profiles.
-- Embeddings are stored as JSONB to keep this migration independent of pgvector.
-- No audio is stored and existing transcript/meeting speaker rows remain valid.
CREATE TABLE IF NOT EXISTS speaker_profiles(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_user_id uuid NOT NULL REFERENCES users(id),
  display_name text NOT NULL,
  embedding_centroid jsonb NOT NULL DEFAULT '[]'::jsonb,
  embedding_dimensions integer NOT NULL DEFAULT 0,
  embedding_model text,
  samples integer NOT NULL DEFAULT 0,
  confidence double precision,
  meetings_count integer NOT NULL DEFAULT 0,
  duration_ms bigint NOT NULL DEFAULT 0,
  status text NOT NULL DEFAULT 'ACTIVE',
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  last_seen_at timestamptz,
  CONSTRAINT speaker_profiles_display_name_not_blank CHECK (length(btrim(display_name)) > 0),
  CONSTRAINT speaker_profiles_embedding_dimensions_valid CHECK (embedding_dimensions BETWEEN 0 AND 2048),
  CONSTRAINT speaker_profiles_samples_valid CHECK (samples >= 0),
  CONSTRAINT speaker_profiles_meetings_valid CHECK (meetings_count >= 0),
  CONSTRAINT speaker_profiles_duration_valid CHECK (duration_ms >= 0),
  CONSTRAINT speaker_profiles_status_valid CHECK (status IN ('ACTIVE','PAUSED','ARCHIVED'))
);

ALTER TABLE meeting_speakers ADD COLUMN IF NOT EXISTS speaker_profile_id uuid REFERENCES speaker_profiles(id);
ALTER TABLE meeting_speakers ADD COLUMN IF NOT EXISTS profile_confidence double precision;
ALTER TABLE meeting_speakers ADD COLUMN IF NOT EXISTS profile_match_status text NOT NULL DEFAULT 'UNMATCHED';
ALTER TABLE meeting_speakers ADD COLUMN IF NOT EXISTS profile_match_reason text;
ALTER TABLE meeting_speakers ADD COLUMN IF NOT EXISTS profile_suggestion_name text;

CREATE INDEX IF NOT EXISTS ix_speaker_profiles_owner_status ON speaker_profiles(owner_user_id, status, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_meeting_speakers_profile ON meeting_speakers(speaker_profile_id);
CREATE INDEX IF NOT EXISTS ix_meeting_speakers_match_status ON meeting_speakers(profile_match_status);
