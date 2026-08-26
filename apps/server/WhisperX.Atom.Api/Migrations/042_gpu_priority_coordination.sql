-- Generalize the coordination row introduced by migration 040.  The legacy
-- ASR columns remain intact for rolling compatibility; new workers write the
-- workload columns and readers fall back to the legacy values.
ALTER TABLE gpu_runtime_coordination
    ADD COLUMN IF NOT EXISTS workload_type TEXT,
    ADD COLUMN IF NOT EXISTS workload_priority INTEGER,
    ADD COLUMN IF NOT EXISTS workload_request_id TEXT,
    ADD COLUMN IF NOT EXISTS workload_job_id UUID,
    ADD COLUMN IF NOT EXISTS workload_owner TEXT,
    ADD COLUMN IF NOT EXISTS workload_requested_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS llm_active BOOLEAN NOT NULL DEFAULT FALSE;

UPDATE gpu_runtime_coordination
SET workload_type = COALESCE(workload_type, CASE WHEN asr_request_id IS NOT NULL THEN 'V1_ASR' END),
    workload_priority = COALESCE(workload_priority, CASE WHEN asr_request_id IS NOT NULL THEN 10 END),
    workload_request_id = COALESCE(workload_request_id, asr_request_id),
    workload_job_id = COALESCE(workload_job_id, asr_job_id),
    workload_owner = COALESCE(workload_owner, asr_owner),
    workload_requested_at = COALESCE(workload_requested_at, asr_requested_at)
WHERE id = 1;

CREATE INDEX IF NOT EXISTS ix_gpu_runtime_coordination_workload
    ON gpu_runtime_coordination (workload_request_id)
    WHERE workload_request_id IS NOT NULL;
