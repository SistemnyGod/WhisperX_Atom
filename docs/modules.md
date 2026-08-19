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
| Desktop | `apps/desktop/WhisperX.Atom.Desktop` | WinUI 3 shell, pages, ViewModels, REST/SSE и Named Pipe client |
| Recorder Core | `apps/recorder-agent` | Capture, FLAC encoding, SQLite spool, archive, session state |
| Recorder Service | `apps/recorder-agent/WhisperX.Atom.Recorder.Service.csproj` | Windows Service host, IPC pipe, background delivery |
| Voice Host | `apps/voice-host` | Wake phrase, локальное распознавание, команды и доставка вопросов через Desktop Broker; не хранит записи и API token |
| Host GPU Worker | `workers/ml_worker` | WhisperX, quality, alignment, diarization, persistence, long-job heartbeat |
| Media Worker | `workers/media_worker` | Probe, assembly, normalization и подготовка ASR audio |
| Import Worker | `workers/import_worker` | Inbox watcher и импорт локальных файлов через internal API |
| Outbox Relay | `workers/outbox_relay` | Доставка durable outbox событий в NATS JetStream |
| Summary Worker | `workers/summary_worker` | Qwen Summary v2 и grounded Assistant; включается независимо флагами Summary/Assistant |
| Shared Python package | `whisperx_atom` | Processing contracts, Core Pipeline facade, ASR/Preprocessing/Alignment/Diarization engine boundaries, state machine jobs, Meeting/Recording/Job/Transcript projections, media storage boundary и общая transcript-quality логика |
| Legacy GUI/watch | `app.py`, `app/`, `auto_transcribe_watch.py` | Совместимость, локальные сценарии и regression surface; live capture использует `live_runtime.SoundDeviceChunkRecorder` с одним `InputStream`; не расширять как новый server pipeline |
| Automation | `scripts/` | Runtime launch/stop/doctor, E2E, watchdog, build и acceptance |
| Tests | `tests/` | Contract, unit, media, recording, assistant и runtime checks |

## Worker subjects и связь

Сервер создаёт durable задания в PostgreSQL/outbox. Outbox Relay публикует их в NATS. Media Worker завершает подготовку media и передаёт транскрипцию GPU Worker. GPU Worker обновляет jobs, transcript и meeting statuses в PostgreSQL. Для долгих задач используются `message.in_progress()`, `jobs.last_heartbeat` и lease renewal.

## Что не следует смешивать

- Recorder capture и WhisperX inference — разные процессы и разные recovery-модели.
- `meetingId` и локальный `recording_session_id` — разные идентификаторы; связь появляется при binding.
- Speaker identity и ответственный за task — разные доменные понятия.
- Transcript readiness и summary readiness — независимые стадии.
- Host GPU и container GPU — альтернативные runtime modes, а не два одновременно работающих worker.
