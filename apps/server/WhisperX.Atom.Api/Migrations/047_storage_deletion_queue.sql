-- Durable physical cleanup for logically deleted meetings.
-- The queue is intentionally additive: a failed File.Delete must remain
-- retryable after the HTTP request and after an API restart.
CREATE TABLE IF NOT EXISTS storage_deletion_queue (
    id BIGSERIAL PRIMARY KEY,
    storage_key TEXT NOT NULL,
    reason TEXT NOT NULL DEFAULT 'MEETING_DELETED',
    attempt INTEGER NOT NULL DEFAULT 0,
    next_retry_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_storage_deletion_queue_pending_key
    ON storage_deletion_queue(storage_key)
    WHERE completed_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_storage_deletion_queue_due
    ON storage_deletion_queue(next_retry_at, created_at)
    WHERE completed_at IS NULL;
