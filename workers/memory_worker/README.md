# Meeting Memory Worker

## Назначение

Индексирует подтверждённые transcript facts в Meeting Memory: entities,
relations, temporal state, threads и invalidation. Memory — производный индекс,
не источник вместо canonical transcript.

## Навигация

- `worker.py` — lease, NATS subscription и job lifecycle.
- `fact_extractor.py` / `entity_resolver.py` — facts/entities.
- `relation_resolver.py` / `temporal_resolver.py` — supersedes/temporal links.
- `thread_builder.py` / `indexer.py` — memory projections.
- `memory_retrieval.py` — scoped retrieval.
- `models.py` — internal contracts.

## Эксплуатация

Worker запускается profile `memory`. Jobs имеют bounded lease/heartbeat и
восстанавливаются после падения. Scope всегда ограничен owner и разрешёнными
meeting IDs. Если evidence неоднозначен, индекс сохраняет uncertainty и не
создаёт подтверждённый факт.

Диагностика выполняется по memory job status и heartbeat; SQL-изменения вручную
не допускаются. Для повторной индексации используйте штатный repair/rebuild
job, сохраняя transcript version.
