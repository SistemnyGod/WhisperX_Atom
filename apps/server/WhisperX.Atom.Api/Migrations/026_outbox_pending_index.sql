CREATE INDEX IF NOT EXISTS ix_outbox_messages_pending_created
  ON outbox_messages(created_at, id)
  WHERE published_at IS NULL;
