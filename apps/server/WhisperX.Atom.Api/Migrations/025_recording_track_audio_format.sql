ALTER TABLE recording_tracks ADD COLUMN IF NOT EXISTS source_encoding text;
ALTER TABLE recording_tracks ADD COLUMN IF NOT EXISTS source_sub_format text;
ALTER TABLE recording_tracks ADD COLUMN IF NOT EXISTS valid_bits_per_sample integer;
