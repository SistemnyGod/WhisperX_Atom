ALTER TABLE recording_tracks ADD COLUMN IF NOT EXISTS device_name text;
ALTER TABLE recording_tracks ADD COLUMN IF NOT EXISTS selection_mode text;
ALTER TABLE recording_tracks ADD COLUMN IF NOT EXISTS recording_profile text;
ALTER TABLE recording_tracks ADD COLUMN IF NOT EXISTS encoding text;
ALTER TABLE recording_tracks ADD COLUMN IF NOT EXISTS bits_per_sample integer;
