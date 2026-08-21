-- Durable liveness is separate from worker heartbeat.  Heartbeats prove that
-- the process is alive; these fields prove that the current stage advances.
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS stage_changed_at timestamptz;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS progress_changed_at timestamptz;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS timeout_requeue_count integer NOT NULL DEFAULT 0;

UPDATE jobs
SET stage_changed_at = COALESCE(stage_changed_at, created_at),
    progress_changed_at = COALESCE(progress_changed_at, created_at)
WHERE stage_changed_at IS NULL OR progress_changed_at IS NULL;

CREATE OR REPLACE FUNCTION whisperx_touch_job_progress()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    NEW.stage_changed_at = COALESCE(NEW.stage_changed_at, now());
    NEW.progress_changed_at = COALESCE(NEW.progress_changed_at, now());
  ELSE
    IF NEW.stage IS DISTINCT FROM OLD.stage THEN
      NEW.stage_changed_at = now();
    ELSE
      NEW.stage_changed_at = COALESCE(OLD.stage_changed_at, now());
    END IF;
    IF NEW.progress IS DISTINCT FROM OLD.progress THEN
      NEW.progress_changed_at = now();
    ELSE
      NEW.progress_changed_at = COALESCE(OLD.progress_changed_at, now());
    END IF;
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_jobs_progress_liveness ON jobs;
CREATE TRIGGER trg_jobs_progress_liveness
BEFORE INSERT OR UPDATE OF stage, progress ON jobs
FOR EACH ROW EXECUTE FUNCTION whisperx_touch_job_progress();

CREATE INDEX IF NOT EXISTS ix_jobs_progress_liveness
  ON jobs(status, stage, stage_changed_at, progress_changed_at)
  WHERE status IN ('QUEUED','RUNNING');
