-- Process-scoped ownership for the shared resident Qwen runtime.
-- Additive only; existing ASR/workload coordination columns remain intact.
ALTER TABLE gpu_runtime_coordination
    ADD COLUMN IF NOT EXISTS llm_owner_heartbeat_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS llm_active_workload TEXT,
    ADD COLUMN IF NOT EXISTS llm_active_request_id TEXT;

UPDATE gpu_runtime_coordination
SET llm_owner_heartbeat_at = COALESCE(llm_owner_heartbeat_at, updated_at)
WHERE llm_owner IS NOT NULL;
