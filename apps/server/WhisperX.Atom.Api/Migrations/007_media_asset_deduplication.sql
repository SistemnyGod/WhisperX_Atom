-- Keep a local media asset for every uploaded meeting while linking
-- repeated content to the canonical asset instead of failing on the SHA index.
ALTER TABLE media_assets ADD COLUMN IF NOT EXISTS duplicate_of uuid REFERENCES media_assets(id);
CREATE INDEX IF NOT EXISTS ix_media_assets_duplicate_of ON media_assets(duplicate_of) WHERE duplicate_of IS NOT NULL;