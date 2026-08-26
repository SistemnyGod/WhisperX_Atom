# API, SSE, TUS и IPC

## Аутентификация и readiness

| Метод | Endpoint | Назначение |
| --- | --- | --- |
| `POST` | `/api/auth/login` | Создание access/refresh сессии |
| `POST` | `/api/auth/refresh` | Вращение refresh-сессии |
| `POST` | `/api/auth/logout` | Отзыв сессий |
| `GET` | `/api/auth/me` | Текущий пользователь |
| `GET` | `/health` | Лёгкая проверка процесса API |
| `GET` | `/ready` | Startup readiness PostgreSQL для Docker |
| `GET` | `/api/system/readiness` | Авторизованная проверка полного transcript runtime |
| `GET` | `/api/system/status` | PostgreSQL и свободное место |

`/api/system/readiness` показывает PostgreSQL, NATS, outbox/import/media/GPU worker heartbeat, CUDA, HF/diarization, optional Recorder и статус Qwen. Дополнительно `queue` содержит durable GPU ownership (`healthyGpuJobs`, `orphanedGpuJobs`, `activeInboxLeases`, `oldestGpuProgressAgeSeconds`), `queuedAsrJobs` и `queuedAssistantQueries`; `reasons` объясняет `gpu_asr_active`, `gpu_job_orphaned` и `assistant_waiting_for_gpu`. `/ready` намеренно остаётся лёгким инфраструктурным probe и не доказывает готовность WhisperX.

## Meetings, uploads и processing

| Метод | Endpoint | Назначение |
| --- | --- | --- |
| `GET/POST` | `/api/meetings` | Реестр и создание встреч |
| `GET` | `/api/meetings/{id}` | Метаданные встречи |
| `POST` | `/api/meetings/{id}/uploads` | Резервация upload |
| `POST` | `/api/uploads/complete` | Завершение загрузки |
| `GET` | `/api/meetings/{id}/jobs` | Jobs встречи |
| `GET` | `/api/jobs/{id}` | Текущее состояние job |
| `GET` | `/api/jobs/{id}/events` | SSE progress, polling fallback в Desktop |
| `POST` | `/api/jobs/{id}/retry` | Повтор failed/cancelled job |
| `POST` | `/api/meetings/{id}/cancel` | Отмена обработки |
| `DELETE` | `/api/meetings/{id}` | Удаление/отмена встречи согласно состоянию |

Transcript endpoint:

```text
GET /api/meetings/{id}/transcript
```

Возвращает `status`, `isPartial`, `warnings`, `qualityWarnings`, `quality`, `qualityScore` и `segments`. `READY` и `PARTIAL_READY` пригодны для открытия пользователем.

## TUS

Desktop использует единый ServerOrigin и LAN gateway на `http://192.168.2.194:8080/files/` (для другого LAN адреса замените origin):

```text
POST /files       создать upload
HEAD /files/{id}  получить Upload-Offset
PATCH /files/{id} отправить очередной блок
```

Размер блока — 16 MiB. При сетевом разрыве клиент повторяет `HEAD`, берёт серверный `Upload-Offset` и продолжает с нужного места. Секрет tus hook остаётся только на стороне конфигурации API/tusd.

## Recorder Named Pipe

Primary AudioGraph Host pipe: `WhisperXAtomRecorderHost`. Current IPC protocol version is `6`.

The legacy Service pipe `WhisperXAtomAgent` remains supported for the explicit `LEGACY_WASAPI` fallback and temporarily accepts protocol version `5`.

| Команда | Назначение |
| --- | --- |
| `STATUS` | Краткое состояние capture |
| `HEALTH` | Устройства, storage, Agent/server state, peaks и backlog |
| `PREFLIGHT` | Проверка microphone, spool, archive и места; FFmpeg/ffprobe возвращаются как отдельная `EncodingReady` capability и не блокируют локальный START |
| `GET_SESSION_STATUS` | Capture/delivery, local/confirmed/pending chunks и server session |
| `CONFIGURE` | Передача Agent URL/id/token и настроек |
| `SET_ARCHIVE_ROOT` | Изменение локального архива |
| `SET_AUDIO_DEVICES` | Выбор microphone и system audio |
| `START` | Начало локальной записи |
| `PAUSE` / `RESUME` | Управление текущей записью |
| `MARKER` / `DECISION` / `ACTION_ITEM` | Сохранение событий в записи |
| `STOP` | Завершение capture и запуск local/server finalization |
| `RETRY_UPLOAD` | Повтор доставки сессии |

`HEALTH` содержит `installationId`, `agentId`, `serverConnectionState`, `lastHeartbeatAtUtc`, `lastServerError`, backlog и список устройств. Технические device ID нужны IPC, но не должны отображаться пользователю напрямую.

## Agent REST

Основные endpoints: `/api/agents`, `/api/agents/link-local`, `/api/v1/agents/enroll`, `/api/v1/agents/{id}/heartbeat`, command SSE и command result. Recording session API находится под `/api/v1/recording-sessions` и поддерживает tracks, chunk upload, missing-chunks, finalize и events batch.

## Assistant и analysis

Существуют conversation endpoints `/api/assistant/conversations`, `/messages`, `/events`, а старый `/api/assistant/queries` сохранён для совместимости. Summary, decisions, tasks и speakers обслуживаются отдельными meeting endpoints. Полный список текущих маршрутов сверяйте с `apps/server/WhisperX.Atom.Api/Program.cs`: это намеренно modular monolith без второго API-контроллера.
