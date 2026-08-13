CREATE UNIQUE INDEX IF NOT EXISTS ux_recording_sessions_agent_local_session
  ON recording_sessions(agent_id, local_session_id)
  WHERE agent_id IS NOT NULL AND local_session_id IS NOT NULL;
