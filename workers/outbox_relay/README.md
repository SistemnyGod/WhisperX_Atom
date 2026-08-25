# Outbox Relay

## Назначение

Публикует durable outbox events в NATS JetStream и восстанавливает события,
потерянные между DB commit и worker ACK. Exactly-once достигается idempotency,
lease и inbox reconciliation, а не повторной записью данных.

## Навигация

- `worker.py` — polling/publish/recovery loop.
- `recover_starved_queued` / `recover_expired` — watchdog repairs.
- `workers/nats_utils.py` — stream/message helpers.
- `workers/runtime_heartbeat.py` — readiness heartbeat.

## Эксплуатация

Запускается profile `core`. Следите за queue age, publish failures, lease и
heartbeat. Outbox можно безопасно повторно публиковать: consumers обязаны
соблюдать idempotent job/message IDs. Не очищайте outbox SQL вручную и не
пересоздавайте stream во время recovery.
