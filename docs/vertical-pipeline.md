# Вертикальная цепочка записи и обработки

## Назначение

Для каждой recorder-сессии система сохраняет одну неизменяемую lineage-цепочку:

```text
recording_session_id
  → meeting_id
  → media_asset_id
  → asr_job_id
  → transcript_v1_id
  → enrichment_job_id
  → transcript_v2_id
  → summary_job_id
  → summary_id
```

Связь хранится в PostgreSQL в `recording_pipeline_runs` (миграция `032_recording_pipeline_lineage.sql`). Она не зависит от NATS-сообщений и поэтому остаётся доступной после перезапуска Desktop, API или любого worker.

## Ответственность модулей

| Этап | Компонент | Гарантия |
|---|---|---|
| START/PCM | Recorder Host/Agent | AudioGraph пишет durable PCM до любой обработки |
| LOCAL_READY | Recorder `SpoolStore` | сессионная timeline-проверка не допускает gap/overlap |
| FLAC/upload | Recorder delivery + API finalize | идемпотентные asset/job и outbox по `job_id` |
| Media | `workers/media_worker` | `media.ingest` повторно ремонтирует отсутствующий `ml.transcribe` outbox |
| ASR V1 | `workers/ml_worker` | `ASR_DRAFT/PARTIAL_READY` публикуется до enrichment |
| V2 | `TRANSCRIPT_ENRICH` | повторная доставка не создаёт второй ENRICHED transcript |
| Summary | `workers/summary_worker` | `summaries.job_id` и lineage защищают от дублей |

## Повторный запуск и сбои

- Повторный `finalize` возвращает исходные `media_asset_id` и `asr_job_id`.
- Media worker при падении после перехода в `READY_FOR_ASR` проверяет outbox по `payload.job_id` и допубликовывает только отсутствующее событие.
- ASR V1 и enrichment разделены разными jobs. Ошибка enrichment переводит встречу в `PARTIAL_READY`, но не удаляет и не скрывает V1.
- Повторный enrichment ищет уже сохранённый V2 по `processing_job_id`/`source_transcript_id` и только закрывает job.
- Summary повторно использует `summaries.job_id`; при ошибке summary стенограмма остаётся доступной.

## Диагностика

Агент может получить цепочку:

```text
GET /api/v1/recording-sessions/{sessionId}/pipeline
```

А пользователь Desktop — цепочки своих доступных встреч:

```text
GET /api/meetings/{meetingId}/pipeline
```

Ответ не содержит аудио, токены или текст стенограммы: только идентификаторы, статусы и correlation ID.

## Вертикальный E2E

Используется `scripts/e2e-vertical-pipeline.ps1`. Скрипт запускает установленный Recorder Host через существующий AudioGraph acceptance gate с включённой серверной доставкой, затем ожидает серверную lineage-цепочку и проверяет все обязательные ID до Summary. Он не включает аудио в acceptance JSON. Для явной проверки восстановления workers можно добавить `-RestartWorkers`; по умолчанию скрипт не изменяет running runtime.

Пример:

```powershell
.\scripts\e2e-vertical-pipeline.ps1 `
  -Seconds 30
```

Результат: `artifacts/acceptance/voice-to-transcript-v1.json`.
