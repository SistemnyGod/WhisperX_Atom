ALTER TABLE recording_sessions ADD COLUMN IF NOT EXISTS finalize_manifest jsonb;
ALTER TABLE recording_sessions ADD COLUMN IF NOT EXISTS manifest_sha256 text;
ALTER TABLE recording_sessions ADD COLUMN IF NOT EXISTS finalized_at timestamptz;

CREATE UNIQUE INDEX IF NOT EXISTS ux_media_assets_recorder_session
  ON media_assets(storage_key)
  WHERE source_type = 'recorder_session' AND storage_key IS NOT NULL;
