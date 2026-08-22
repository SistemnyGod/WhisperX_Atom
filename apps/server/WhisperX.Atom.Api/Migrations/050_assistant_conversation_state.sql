-- Structured follow-up context. This stores query understanding only; it is
-- not evidence and must never be used as a factual source for an answer.
CREATE TABLE IF NOT EXISTS assistant_conversation_state(
    conversation_id uuid PRIMARY KEY REFERENCES assistant_conversations(id) ON DELETE CASCADE,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    last_meeting_id uuid REFERENCES meetings(id) ON DELETE SET NULL,
    last_intent text,
    last_topic text,
    last_person text,
    last_date_range text,
    last_user_question text,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_assistant_conversation_state_user_updated
    ON assistant_conversation_state(user_id, updated_at DESC);

