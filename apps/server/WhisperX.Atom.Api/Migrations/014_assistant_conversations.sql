CREATE TABLE IF NOT EXISTS assistant_conversations(
  id uuid PRIMARY KEY,
  user_id uuid REFERENCES users(id),
  title text NOT NULL,
  scope_type text NOT NULL CHECK (scope_type IN ('MEETING','GLOBAL')),
  meeting_id uuid REFERENCES meetings(id),
  archived_at timestamptz,
  deleted_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK ((scope_type = 'MEETING' AND meeting_id IS NOT NULL) OR (scope_type = 'GLOBAL' AND meeting_id IS NULL))
);

CREATE INDEX IF NOT EXISTS ix_assistant_conversations_user_updated
  ON assistant_conversations(user_id, deleted_at, archived_at, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_assistant_conversations_meeting
  ON assistant_conversations(meeting_id, deleted_at, updated_at DESC);

CREATE TABLE IF NOT EXISTS assistant_messages(
  id uuid PRIMARY KEY,
  conversation_id uuid NOT NULL REFERENCES assistant_conversations(id) ON DELETE CASCADE,
  role text NOT NULL CHECK (role IN ('USER','ASSISTANT')),
  content text NOT NULL,
  status text NOT NULL DEFAULT 'READY',
  voice_answer text,
  evidence jsonb NOT NULL DEFAULT '[]'::jsonb,
  error_code text,
  retry_of uuid REFERENCES assistant_messages(id),
  created_at timestamptz NOT NULL DEFAULT now(),
  completed_at timestamptz
);

CREATE INDEX IF NOT EXISTS ix_assistant_messages_conversation
  ON assistant_messages(conversation_id, created_at, id);

ALTER TABLE assistant_queries ADD COLUMN IF NOT EXISTS conversation_id uuid REFERENCES assistant_conversations(id);
ALTER TABLE assistant_queries ADD COLUMN IF NOT EXISTS user_message_id uuid REFERENCES assistant_messages(id);
ALTER TABLE assistant_queries ADD COLUMN IF NOT EXISTS assistant_message_id uuid REFERENCES assistant_messages(id);
CREATE INDEX IF NOT EXISTS ix_assistant_queries_conversation ON assistant_queries(conversation_id, created_at);
