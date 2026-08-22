-- Cross-meeting relations are derived hints and retain their evidence-backed
-- source facts.  They are invalidated instead of deleted after reindexing.
CREATE TABLE IF NOT EXISTS memory_fact_relations(
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    source_fact_id uuid NOT NULL REFERENCES transcript_facts(id) ON DELETE CASCADE,
    target_fact_id uuid NOT NULL REFERENCES transcript_facts(id) ON DELETE CASCADE,
    relation_type text NOT NULL,
    confidence numeric(5,4) NOT NULL DEFAULT 0.0,
    derivation_type text NOT NULL DEFAULT 'DERIVED',
    created_at timestamptz NOT NULL DEFAULT now(),
    invalidated_at timestamptz,
    CHECK(source_fact_id <> target_fact_id),
    UNIQUE(source_fact_id, target_fact_id, relation_type)
);

CREATE INDEX IF NOT EXISTS ix_memory_fact_relations_source
    ON memory_fact_relations(source_fact_id, relation_type, invalidated_at);
CREATE INDEX IF NOT EXISTS ix_memory_fact_relations_target
    ON memory_fact_relations(target_fact_id, relation_type, invalidated_at);
