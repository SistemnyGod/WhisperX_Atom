-- WhisperX Atom server-first schema (idempotent bootstrap migration)
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE TABLE IF NOT EXISTS users(
  id uuid PRIMARY KEY,
  username text NOT NULL UNIQUE,
  password_hash text NOT NULL,
  role text NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS sessions(
  id uuid PRIMARY KEY,
  user_id uuid NOT NULL REFERENCES users(id),
  token_hash text NOT NULL UNIQUE,
  expires_at timestamptz NOT NULL,
  revoked_at timestamptz
);
CREATE TABLE IF NOT EXISTS meetings(
  id uuid PRIMARY KEY,
  title text NOT NULL,
  description text,
  status text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS media_assets(
  id uuid PRIMARY KEY,
  meeting_id uuid NOT NULL REFERENCES meetings(id),
  upload_id uuid UNIQUE,
  original_name text NOT NULL,
  storage_key text,
  sha256 text,
  size_bytes bigint NOT NULL DEFAULT 0,
  duration_ms bigint,
  status text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE media_assets ADD COLUMN IF NOT EXISTS archive_storage_key text;
ALTER TABLE media_assets ADD COLUMN IF NOT EXISTS preview_storage_key text;
ALTER TABLE media_assets ADD COLUMN IF NOT EXISTS asr_storage_key text;
ALTER TABLE media_assets ADD COLUMN IF NOT EXISTS source_type text NOT NULL DEFAULT 'upload';
CREATE TABLE IF NOT EXISTS jobs(
  id uuid PRIMARY KEY,
  meeting_id uuid NOT NULL REFERENCES meetings(id),
  media_asset_id uuid REFERENCES media_assets(id),
  type text NOT NULL,
  status text NOT NULL,
  stage text NOT NULL,
  progress integer NOT NULL DEFAULT 0,
  attempt integer NOT NULL DEFAULT 0,
  error_message text,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS job_attempts(
  id uuid PRIMARY KEY,
  job_id uuid NOT NULL REFERENCES jobs(id),
  attempt integer NOT NULL,
  stage text NOT NULL,
  status text NOT NULL,
  started_at timestamptz,
  finished_at timestamptz,
  error_message text
);
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS worker_id text;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS last_heartbeat timestamptz;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS error_code text;
CREATE TABLE IF NOT EXISTS transcripts(
  id uuid PRIMARY KEY,
  meeting_id uuid NOT NULL REFERENCES meetings(id),
  version integer NOT NULL,
  status text NOT NULL,
  language text,
  model_name text,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(meeting_id, version)
);
CREATE TABLE IF NOT EXISTS meeting_speakers(
  id uuid PRIMARY KEY,
  meeting_id uuid NOT NULL REFERENCES meetings(id),
  stable_key text NOT NULL,
  display_name text NOT NULL,
  confidence double precision,
  UNIQUE(meeting_id, stable_key)
);
CREATE TABLE IF NOT EXISTS transcript_segments(
  id uuid PRIMARY KEY,
  transcript_id uuid NOT NULL REFERENCES transcripts(id),
  ordinal integer NOT NULL,
  start_ms bigint NOT NULL,
  end_ms bigint NOT NULL,
  speaker_id uuid REFERENCES meeting_speakers(id),
  speaker_label text,
  text text NOT NULL,
  confidence double precision,
  words jsonb,
  UNIQUE(transcript_id, ordinal)
);
CREATE TABLE IF NOT EXISTS outbox_messages(
  id uuid PRIMARY KEY,
  topic text NOT NULL,
  payload jsonb NOT NULL,
  published_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS inbox_messages(
  message_id uuid PRIMARY KEY,
  received_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_meetings_created_at ON meetings(created_at DESC);
CREATE INDEX IF NOT EXISTS ix_jobs_meeting ON jobs(meeting_id, created_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_asset_type ON jobs(media_asset_id, type) WHERE media_asset_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_media_assets_sha256 ON media_assets(sha256) WHERE sha256 IS NOT NULL AND sha256 <> '';
CREATE INDEX IF NOT EXISTS ix_transcript_segments_text ON transcript_segments USING gin(to_tsvector('simple', text));




