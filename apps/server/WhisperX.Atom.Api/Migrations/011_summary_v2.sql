ALTER TABLE summaries ADD COLUMN IF NOT EXISTS schema_version text NOT NULL DEFAULT 'summary-v1';
ALTER TABLE summaries ADD COLUMN IF NOT EXISTS quality_score numeric;

CREATE TABLE IF NOT EXISTS summary_runs(
  id uuid PRIMARY KEY,
  summary_id uuid NOT NULL REFERENCES summaries(id) ON DELETE CASCADE,
  model_name text NOT NULL,
  model_file_hash text,
  prompt_version text NOT NULL,
  schema_version text NOT NULL,
  temperature numeric,
  context_size integer,
  source_hash text NOT NULL,
  started_at timestamptz NOT NULL DEFAULT now(),
  finished_at timestamptz,
  block_count integer NOT NULL DEFAULT 0,
  input_tokens integer,
  output_tokens integer,
  generation_ms bigint,
  quality_score numeric,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_summary_runs_summary_created
  ON summary_runs(summary_id, created_at DESC);
