# Модули и зоны ответственности

Расширенное описание lifecycle и границ каждого runtime-модуля находится в
[module-reference.md](module-reference.md). Поток raw-first записи, состояния
durability и безопасные точки рефакторинга описаны в
[recording-module-reference.md](recording-module-reference.md).
Архитектурный контракт Core Pipeline, state machine jobs и порядок миграции
описаны в [architecture-incremental.md](architecture-incremental.md).

| Модуль | Расположение | Ответственность |
| --- | --- | --- |
| Control API | `apps/server/WhisperX.Atom.Api` | Auth, meetings, uploads, jobs, transcript, speakers, summaries, assistant, agents, audit |
| AssistantModeResolver | `apps/server/WhisperX.Atom.Api/AssistantModeResolver.cs` | Единая retrieval-aware маршрутизация AUTO/GENERAL_CHAT/CURRENT_MEETING/MEETING_MEMORY/LIVE_MEETING для текста и голоса; scope follow-up, FTS quality/RBAC probes и live readiness |
| Desktop | `apps/desktop/WhisperX.Atom.Desktop` | WinUI 3 shell, pages, ViewModels, REST/SSE и Named Pipe client |
| Recorder Core | `apps/recorder-agent` | Capture, FLAC encoding, SQLite spool, archive, session state |
| Recorder Service | `apps/recorder-agent/WhisperX.Atom.Recorder.Service.csproj` | Windows Service host, IPC pipe, background delivery |
| Voice Host | `apps/voice-host` | Wake phrase, локальное распознавание, команды и доставка вопросов через Desktop Broker; не хранит записи и API token |
| Host GPU Worker | `workers/ml_worker` | WhisperX, quality, alignment, diarization, persistence, long-job heartbeat |
| Media Worker | `workers/media_worker` | Probe, assembly, normalization и подготовка ASR audio |
| Import Worker | `workers/import_worker` | Inbox watcher и импорт локальных файлов через internal API |
| Outbox Relay | `workers/outbox_relay` | Доставка durable outbox событий в NATS JetStream |
| Summary Worker | `workers/summary_worker` | Qwen Summary v2 и grounded Assistant; включается независимо флагами Summary/Assistant |
| Shared Python package | `whisperx_atom` | Processing contracts, Core Pipeline facade, ASR/Preprocessing/Alignment/Diarization/Postprocessing engine boundaries, typed StageResult, atomic derived-stage checkpoints, bounded GPU scheduler, state machine jobs, Meeting/Recording/Job/Transcript projections, media storage boundary и общая transcript-quality логика |
| Legacy GUI/watch | `app.py`, `app/`, `auto_transcribe_watch.py` | Deprecated compatibility/regression surface only; bounded callback capture, fail-closed restart handling and safe diarization fallback; не расширять как новый server pipeline |
| Automation | `scripts/` | Runtime launch/stop/doctor, E2E, watchdog, build и acceptance |
| Tests | `tests/` | Contract, unit, media, recording, assistant и runtime checks |

## Локальная документация модулей

В каждом runtime-каталоге есть короткий `README.md` с навигацией по entrypoint,
операциями запуска/остановки, диагностикой и границами безопасности:

- [Desktop](../apps/desktop/README.md), [Desktop module](../apps/desktop/WhisperX.Atom.Desktop/README.md)
- [Recorder Agent](../apps/recorder-agent/README.md) и [Recorder Host](../apps/recorder-host/README.md)
- [Server](../apps/server/README.md) и [Control API](../apps/server/WhisperX.Atom.Api/README.md)
- [Voice Host](../apps/voice-host/README.md), [Voice Core](../apps/voice-host/WhisperX.Atom.Voice.Core/README.md), [Refiner Host](../apps/voice-host/WhisperX.Atom.Voice.Refiner.Host/README.md)
- [TtsHost](../apps/tts-host/README.md) и [Web diagnostics](../apps/web/README.md)
- [Import](../workers/import_worker/README.md), [Media](../workers/media_worker/README.md), [GPU](../workers/ml_worker/README.md), [Memory](../workers/memory_worker/README.md), [Outbox](../workers/outbox_relay/README.md), [Summary/Assistant](../workers/summary_worker/README.md)
- [Shared Python core](../whisperx_atom/README.md), [legacy local API](../app/README.md), [scripts](../scripts/README.md), [tests](../tests/README.md)

## Worker subjects и связь

Сервер создаёт durable задания в PostgreSQL/outbox. Outbox Relay публикует их в NATS. Media Worker завершает подготовку media и передаёт транскрипцию GPU Worker. GPU Worker обновляет jobs, transcript и meeting statuses в PostgreSQL. Для долгих задач используются `message.in_progress()`, `jobs.last_heartbeat` и lease renewal.

## Что не следует смешивать

- Recorder capture и WhisperX inference — разные процессы и разные recovery-модели.
- `meetingId` и локальный `recording_session_id` — разные идентификаторы; связь появляется при binding.
- Speaker identity и ответственный за task — разные доменные понятия.
- Transcript readiness и summary readiness — независимые стадии.
- Host GPU и container GPU — альтернативные runtime modes, а не два одновременно работающих worker.
