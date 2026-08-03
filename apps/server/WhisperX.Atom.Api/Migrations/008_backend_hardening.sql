-- Serialize command cursors per Agent in the application and enforce uniqueness at the database boundary.
CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_commands_agent_cursor ON agent_commands(agent_id, cursor);
-- Keep polling efficient when the Agent reconnects with its durable cursor.
CREATE INDEX IF NOT EXISTS ix_agent_commands_pending_cursor ON agent_commands(agent_id, status, cursor);