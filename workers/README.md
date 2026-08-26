# Server workers

| Worker | README | Queue/role |
| --- | --- | --- |
| Import | [import_worker](import_worker/README.md) | inbox → import jobs |
| Media | [media_worker](media_worker/README.md) | chunks/assets → archive/ASR |
| GPU | [ml_worker](ml_worker/README.md) | WhisperX V1/V2, CUDA lease |
| Summary/Assistant | [summary_worker](summary_worker/README.md) | Qwen summary and Assistant |
| Memory | [memory_worker](memory_worker/README.md) | Meeting Memory index |
| Outbox | [outbox_relay](outbox_relay/README.md) | PostgreSQL outbox → NATS |

Workers запускаются только canonical Compose/Server Bundle profiles. Воркеры
используют heartbeat, lease и idempotency; ручной запуск второго экземпляра
требует отдельного диагностического сценария.
