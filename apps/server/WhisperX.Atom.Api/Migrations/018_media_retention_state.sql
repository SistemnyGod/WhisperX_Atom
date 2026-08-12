-- Retention is state based.  A worker may only reclaim derived server files
-- after a READY media asset has an explicit deadline; job/media failures are
-- intentionally absent from this eligible view.
ALTER TABLE media_assets ADD COLUMN IF NOT EXISTS derived_purge_after timestamptz;
ALTER TABLE media_assets ADD COLUMN IF NOT EXISTS derived_purged_at timestamptz;

CREATE INDEX IF NOT EXISTS ix_media_assets_derived_purge
  ON media_assets(derived_purge_after)
  WHERE derived_purge_after IS NOT NULL AND derived_purged_at IS NULL;

CREATE OR REPLACE VIEW media_asset_derived_purge_candidates AS
SELECT m.id, m.storage_key, m.archive_storage_key, m.preview_storage_key, m.asr_storage_key, m.derived_purge_after
FROM media_assets m
WHERE m.status = 'READY'
  AND m.derived_purge_after IS NOT NULL
  AND m.derived_purge_after <= now()
  AND m.derived_purged_at IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM jobs j
      WHERE j.media_asset_id = m.id
        AND j.status IN ('QUEUED','RUNNING','RETRY_WAIT')
  );
