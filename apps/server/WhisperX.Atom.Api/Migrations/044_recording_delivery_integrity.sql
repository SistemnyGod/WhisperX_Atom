-- Recording delivery integrity and recovery metadata.
--
-- local_track_id is supplied by the recorder and is stable for the lifetime
-- of a local track.  It is nullable so legacy server tracks remain readable;
-- only new bindings participate in the idempotency index.
ALTER TABLE recording_tracks
  ADD COLUMN IF NOT EXISTS local_track_id text;

CREATE UNIQUE INDEX IF NOT EXISTS ux_recording_tracks_session_local_track
  ON recording_tracks(session_id, local_track_id)
  WHERE local_track_id IS NOT NULL;

ALTER TABLE media_assets
  ADD COLUMN IF NOT EXISTS failure_code text;
ALTER TABLE media_assets
  ADD COLUMN IF NOT EXISTS failure_detail text;
