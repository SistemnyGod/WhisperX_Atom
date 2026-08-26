-- Meeting Memory v2: owner-scoped normalized entities and fact links.
-- Entities are an index over transcript_facts; they are never evidence.
CREATE TABLE IF NOT EXISTS memory_entities(
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    entity_type text NOT NULL,
    canonical_name text NOT NULL,
    normalized_name text NOT NULL,
    aliases jsonb NOT NULL DEFAULT '[]'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(owner_user_id, entity_type, normalized_name)
);

CREATE INDEX IF NOT EXISTS ix_memory_entities_owner_name
    ON memory_entities(owner_user_id, entity_type, normalized_name);

CREATE TABLE IF NOT EXISTS fact_entities(
    fact_id uuid NOT NULL REFERENCES transcript_facts(id) ON DELETE CASCADE,
    entity_id uuid NOT NULL REFERENCES memory_entities(id) ON DELETE CASCADE,
    role text NOT NULL,
    confidence numeric(5,4),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(fact_id, entity_id, role)
);

CREATE INDEX IF NOT EXISTS ix_fact_entities_entity_role
    ON fact_entities(entity_id, role, fact_id);
