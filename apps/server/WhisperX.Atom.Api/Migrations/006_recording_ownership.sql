CREATE UNIQUE INDEX IF NOT EXISTS ux_recording_tracks_id_session
  ON recording_tracks(id, session_id);

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'fk_recording_chunks_track_session'
  ) THEN
    ALTER TABLE recording_chunks
      ADD CONSTRAINT fk_recording_chunks_track_session
      FOREIGN KEY (track_id, session_id)
      REFERENCES recording_tracks(id, session_id);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_recording_sessions_agent_id ON recording_sessions(agent_id, id);
