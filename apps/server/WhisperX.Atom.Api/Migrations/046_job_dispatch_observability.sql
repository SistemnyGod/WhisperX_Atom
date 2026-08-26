-- Additive dispatch observability for queued transcription and enrichment jobs.
-- Existing clients may ignore these columns; the durable jobs row remains the
-- source of truth for scheduling and worker ownership.
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS queue_entered_at timestamptz;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS worker_claimed_at timestamptz;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS scheduled_reason text;

UPDATE jobs
SET queue_entered_at = COALESCE(queue_entered_at, stage_changed_at, created_at)
WHERE status = 'QUEUED' AND queue_entered_at IS NULL;

UPDATE jobs
SET worker_claimed_at = COALESCE(worker_claimed_at, last_heartbeat, updated_at)
WHERE status = 'RUNNING' AND worker_claimed_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_jobs_dispatch_state
  ON jobs(status, not_before, queue_entered_at, created_at);

CREATE OR REPLACE FUNCTION whisperx_jobs_dispatch_observability()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
  IF NEW.status = 'QUEUED' THEN
    IF TG_OP = 'INSERT' THEN
      NEW.queue_entered_at := COALESCE(NEW.queue_entered_at, now());
    ELSIF OLD.status IS DISTINCT FROM 'QUEUED' OR NEW.queue_entered_at IS NULL THEN
      NEW.queue_entered_at := COALESCE(NEW.queue_entered_at, now());
    END IF;
    IF TG_OP = 'INSERT' THEN
      NEW.worker_claimed_at := NULL;
    ELSIF NEW.status IS DISTINCT FROM OLD.status OR NEW.worker_id IS NULL THEN
      NEW.worker_claimed_at := NULL;
    END IF;
    IF NEW.not_before IS NOT NULL AND NEW.not_before > now() AND NEW.scheduled_reason IS NULL THEN
      NEW.scheduled_reason := 'TRANSCRIPTION_DELAY';
    ELSIF NEW.not_before IS NULL THEN
      NEW.scheduled_reason := NULL;
    END IF;
  ELSIF NEW.status = 'RUNNING' THEN
    IF TG_OP = 'INSERT' THEN
      NEW.worker_claimed_at := COALESCE(NEW.worker_claimed_at, now());
    ELSIF OLD.status IS DISTINCT FROM 'RUNNING' OR OLD.worker_id IS DISTINCT FROM NEW.worker_id THEN
      NEW.worker_claimed_at := COALESCE(NEW.worker_claimed_at, now());
    END IF;
    NEW.scheduled_reason := NULL;
  ELSIF NEW.status IN ('READY', 'FAILED', 'CANCELLED') THEN
    NEW.not_before := NULL;
    NEW.scheduled_reason := NULL;
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_jobs_dispatch_observability ON jobs;
CREATE TRIGGER trg_jobs_dispatch_observability
BEFORE INSERT OR UPDATE OF status, worker_id, not_before, scheduled_reason ON jobs
FOR EACH ROW EXECUTE FUNCTION whisperx_jobs_dispatch_observability();

-- A previous rollout could have left jobs behind the fixed five-minute defer.
-- Only ASR-ready queued jobs are released; retry backoff for other stages is
-- not changed.
UPDATE jobs
SET not_before = NULL,
    scheduled_reason = NULL
WHERE status = 'QUEUED'
  AND stage = 'READY_FOR_ASR'
  AND not_before IS NOT NULL
  AND not_before > now();
