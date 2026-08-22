-- Voice Assistant requests are retried after an HTTP response can be lost.
-- Keep the request key typed and durable so a retry returns the original
-- query instead of creating a second conversation/outbox message.
ALTER TABLE assistant_queries
    ADD COLUMN IF NOT EXISTS command_id TEXT,
    ADD COLUMN IF NOT EXISTS trace_id TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS ux_assistant_queries_voice_command
    ON assistant_queries(user_id, source, command_id)
    WHERE source = 'VOICE' AND command_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_assistant_queries_voice_command_lookup
    ON assistant_queries(user_id, command_id)
    WHERE source = 'VOICE' AND command_id IS NOT NULL;
