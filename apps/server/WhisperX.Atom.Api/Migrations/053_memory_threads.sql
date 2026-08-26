-- A thread is a durable owner-scoped topic projection over facts.
CREATE TABLE IF NOT EXISTS memory_threads(
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    title text NOT NULL,
    normalized_title text NOT NULL,
    primary_entity_id uuid REFERENCES memory_entities(id) ON DELETE SET NULL,
    state text NOT NULL DEFAULT 'UNKNOWN',
    first_seen_at timestamptz,
    last_seen_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(owner_user_id, normalized_title)
);

CREATE INDEX IF NOT EXISTS ix_memory_threads_owner_state
    ON memory_threads(owner_user_id, state, last_seen_at DESC);

CREATE TABLE IF NOT EXISTS memory_thread_facts(
    thread_id uuid NOT NULL REFERENCES memory_threads(id) ON DELETE CASCADE,
    fact_id uuid NOT NULL REFERENCES transcript_facts(id) ON DELETE CASCADE,
    sequence integer NOT NULL DEFAULT 0,
    role text NOT NULL DEFAULT 'UPDATE',
    PRIMARY KEY(thread_id, fact_id)
);

CREATE INDEX IF NOT EXISTS ix_memory_thread_facts_fact
    ON memory_thread_facts(fact_id, thread_id);
