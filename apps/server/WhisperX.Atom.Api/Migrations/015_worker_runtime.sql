CREATE TABLE IF NOT EXISTS worker_instances(
  worker_name text NOT NULL,
  instance_id text NOT NULL,
  status text NOT NULL DEFAULT 'STARTING',
  last_seen_at timestamptz NOT NULL DEFAULT now(),
  current_job_id uuid,
  version text NOT NULL DEFAULT 'unknown',
  capabilities jsonb NOT NULL DEFAULT '{}'::jsonb,
  last_error_code text,
  PRIMARY KEY(worker_name, instance_id)
);

CREATE INDEX IF NOT EXISTS ix_worker_instances_name_seen
  ON worker_instances(worker_name, last_seen_at DESC);
