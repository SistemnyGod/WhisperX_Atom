# Справочник модулей WhisperX Atom

Сначала прочитайте [системное руководство](system-guide.md): оно объясняет
размещение процессов, пользовательские потоки, хранилища, конфигурацию и
операционный порядок. Этот файл оставлен как более подробная low-level
справка по границам исходных модулей.

Здесь зафиксированы ответственность, границы данных и точки расширения всех
runtime-модулей проекта. При изменении модуля обновляйте соответствующий
раздел и запускайте его contract/regression tests.

## Клиент Windows

### `apps/desktop/WhisperX.Atom.Desktop`

WinUI 3 shell, навигация, страницы, ViewModel, REST/SSE-клиент и Desktop
Broker. Desktop не пишет PCM напрямую: команды записи передаются Recorder
Host/Core. Он хранит пользовательские настройки и локальный update state, но
не хранит серверные токены в диагностике.

Ключевые границы: `ServerApiClient` — серверный HTTP, `VoiceHostController` —
локальный Voice Host, `RecordingCommandService` — единый путь кнопочных и
голосовых START/STOP, `ClientUpdateService` — проверка и staged installer.

### `apps/voice-host/WhisperX.Atom.Voice.Core`

Контракты wake word, Vosk, intent, command session, pre-roll и TTS lifecycle.
Не имеет доступа к meeting transcript и серверному API token.

### `apps/voice-host/WhisperX.Atom.Voice.Host`

Managed process для локального микрофона и Windows TTS. Доставляет команды в
Desktop Broker, воспроизводит подтверждения и публикует technical timeline.
При падении Voice Host Recorder не должен останавливаться.

Во время активной записи вопросы не переводятся в канонический `CURRENT_MEETING`.
Desktop Broker использует отдельный `LIVE_MEETING` scope, а сервер принимает
только свежие text-only provisional-сегменты через
`POST /api/assistant/live-segments/{meetingId}`. Если producer live-ASR ещё не
публиковал сегменты, ответ завершается `LIVE_MEETING_NOT_READY`; V1/V2 и история
не подмешиваются.

### `apps/desktop/Updater`

Отдельный процесс staged update. Проверяет hash/identity, ждёт Desktop,
проверяет активную запись, запускает Inno Setup с UAC и выполняет rollback.
Не завершает неизвестные процессы и не трогает ProgramData/spool/archive.

### `apps/desktop/Installer`

Inno Setup payload и pre/post-install PowerShell. Копирует только программные
файлы, сохраняет service command line, user settings и DPAPI credentials.
Изменение установщика не должно включать автоматическую перезагрузку Windows.

## Локальный Recorder

Подробный контракт см. в [recording-module-reference.md](recording-module-reference.md).

Входной тракт поддерживает независимые потоки Windows AudioGraph и render
loopback. `AudioGraphCaptureEngine` владеет callback и sample-clock для
`room-microphone`, а `SystemAudioCaptureEngine` — отдельным WASAPI loopback
sample-clock для `system-audio`.
`AudioFrameContinuityValidator` до durable writer отклоняет gaps, overlaps,
смену формата и несовпадение размера PCM, а `AudioGraphSessionWriter` сохраняет
raw PCM без FFmpeg, SQLite-запросов и сети внутри callback. Ошибки capture и
writer сходятся в один идемпотентный путь восстановления Recorder Host, который
определяет `LOCAL_READY`, `RECOVERY_PENDING` или `LOCAL_FAILED` по фактически
сохранённым данным. Сведение дорожек выполняется только после локальной
записи и доставки, никогда внутри realtime callback.

### `apps/recorder-agent`

Recorder Core и host-specific orchestration: AudioGraph/WASAPI adapters,
SQLite spool, raw recovery, FLAC encoder, local archive, server binding и
delivery. Offline-first: durable PCM создаётся до сетевых действий.

### `apps/recorder-host`

Current-user AudioGraph Host и IPC endpoint. Владеет реальным capture/render
endpoint, двумя независимыми track writer-ами, health snapshot и process
guard; не владеет серверными jobs и не смешивает microphone/system PCM.

### `apps/recorder-agent/WhisperX.Atom.Recorder.Service.csproj`

Legacy Windows Service host. Оставлен для совместимости и диагностики; не
запускается параллельно с current-user Recorder Host.

## Серверная вертикальная обработка

### `workers/media_worker`

Собирает durable recorder chunks в FLAC/ASR derivatives и передаёт job в GPU
через outbox. Переход `READY_FOR_ASR` идемпотентен: после сбоя между сменой
стадии и публикацией следующая доставка восстанавливает отсутствующее событие
по `payload.job_id`.

### `workers/ml_worker`

Сначала сохраняет `TRANSCRIPT_ENRICH` независимый V1 (`ASR_DRAFT`), затем
создаёт отдельный enrichment job для alignment/diarization. Повторная доставка
ищет V1/V2 по job/source transcript и не создаёт новую версию. Ошибка
enrichment оставляет V1 доступным как `PARTIAL_READY`.

Worker входит в pipeline через `whisperx_atom.core_pipeline` и
`WhisperXRuntime`. `ProcessingService` получает `LegacyPipelineStageAdapter`
для вызовов стадий (preprocessing, ASR, alignment, diarization и
postprocessing); он не импортирует `app.transcription_pipeline` и не вызывает
его private-методы. Adapter — временная compatibility boundary, принадлежащая
runtime. Каждую стадию можно заменить native engine отдельно, не меняя NATS,
API, database и контракты Transcript V1/V2.

### `workers/summary_worker`

Формирует Summary только из V2. `summaries.job_id` и lineage предотвращают
дубли после рестарта; ошибка Summary не удаляет стенограмму.

Подробная сквозная схема и контракты находятся в
[vertical-pipeline.md](vertical-pipeline.md).

### `live_runtime.py` и legacy live UI в `app.py`

Совместимый локальный Tk-сценарий, не используемый установленным Desktop
Recorder Host. `SoundDeviceChunkRecorder` открывает один callback-based
`InputStream` на всю сессию. Обычная запись и финальный drain используют один
chunk splitter: принятые перед STOP или ошибкой stream блоки сначала атомарно
дописываются, а затем публикуется ошибка. Невыровненный PCM отклоняется явно и
никогда не усекается молча.
Bounded blocks пишутся на диск в `.part`; WAV header и durable `fsync`
периодически обновляются, а готовые файлы публикуются атомарным
переименованием. Ротация файлов служит только очереди ASR; media offset
вычисляется по sample count и не включает паузу. Ошибка overrun или
потеря stream переводится в явный failure, а не скрывается как пустая
стенограмма. Этот путь сохраняется для regression compatibility и не должен
получать новые server/API обязанности.

## Сервер API и workers

### `apps/server/WhisperX.Atom.Api`

ASP.NET API: authentication/RBAC, meetings, imports, recording finalize,
media/jobs, Transcript V1/V2, Summary, Assistant, updates и readiness.
API проверяет scope пользователя до retrieval; `CURRENT_MEETING` не может
получить evidence другой встречи.

`LIVE_MEETING` хранит provisional-сегменты в `live_meeting_segments` на всём
интервале записи и переходе `STOP → FINALIZING → V1`. У новых строк есть
семидневный safety-expiry и политика `UNTIL_V1_READY`; при пригодном V1
очистка происходит автоматически. Live evidence фиксируется отдельно от
`assistant_query_evidence`, а после V1 тот же `conversationId` бесшовно
переключается на канонический V1/V2. Строки, на которые ссылается evidence
snapshot, сохраняются для аудита.

### `workers/media_worker`

Проверяет входные media, собирает FLAC tracks, создаёт preview и canonical
16 kHz mono ASR WAV, измеряет сигнал и передаёт `TRANSCRIBE_ASR`. Не меняет
оригинал и не блокирует локальную запись.

### `workers/ml_worker`

WhisperX `large-v3`, русский language contract, ASR quality gate, alignment,
diarization, technical-event masking и persistence Transcript V1/V2. V1
публикуется раньше optional enrichment; плохое V2 не удаляет V1.

### `workers/summary_worker`

Qwen3-8B Summary и grounded Assistant. Берёт evidence только из разрешённого
scope, фиксирует retrieval до вызова модели и валидирует claims после ответа.
Summary запускается после качественного V2; `NEEDS_REVIEW` блокирует summary.

### `AssistantModeResolver`

`apps/server/WhisperX.Atom.Api/AssistantModeResolver.cs` — единый владелец
контекстной маршрутизации Assistant. Получает вопрос, запрошенный режим,
пользователя, активную встречу и состояние записи; возвращает
`resolvedMode`, `meetingId`, `reason`, `confidence` и optional `conversationId`.
Для `AUTO` сначала восстанавливается scope follow-up, но только при новом
сильном retrieval-совпадении. Затем проверяется live-память активной или
финализируемой сессии (до пригодного V1), после неё — текущая встреча, затем
history-поиск для history-like вопросов, и только после отсутствия evidence
выбирается `GENERAL_CHAT`. После V1 тот же follow-up переключается на
канонический transcript. Пробы выполняются
через Russian FTS и те же transcript quality/RBAC-фильтры, что и worker; одних
ключевых слов для выбора meeting scope недостаточно. Ошибка
`LIVE_MEETING_NOT_READY` закрывает только явно запрошенный live-запрос, а
обычный `AUTO` безопасно деградирует в общий чат.

### `workers/import_worker`

Обрабатывает импорт аудио/видео из inbox, создаёт media job через API и
передаёт его в тот же media → ASR → V2 → Summary pipeline. Файл не считается
стенограммой до успешной canonical-проверки.

### `workers/outbox_relay`

Доставляет durable PostgreSQL outbox в NATS JetStream. Повторяет сообщения
идемпотентно и не содержит бизнес-логики распознавания. При старте relay
восстанавливает просроченные `RUNNING` jobs стадий `TRANSCRIBE_ASR`,
`TRANSCRIPT_ENRICH` и `SUMMARIZE`, если для них нет живого inbox lease. Он
создаёт новое событие по тому же `job_id`; downstream использует job/transcript
идентификаторы как ключи идемпотентности, поэтому повтор не создаёт второй
asset, V1, V2 или Summary. Исходные media и Transcript V1 при этом не
перезаписываются.

### `whisperx_atom/`

Общие Python-контракты обработки: анализ сигнала, language/quality metadata,
`AsrEngine`, `PreprocessingEngine`, `AlignmentEngine`, `DiarizationEngine`,
`PostprocessingEngine`, `StageResult`, `GpuScheduler`, canonical provenance и маленькие
чистые функции, пригодные для unit-тестов. `GpuScheduler` ограничивает
конкуренцию GPU внутри процесса; межпроцессная эксклюзивность остаётся в
PostgreSQL lease. `PostprocessingEngine` отделяет нормализацию/glossary от
legacy WhisperX pipeline и не меняет содержимое исходного аудио.
`StageResult` хранит только имя стадии, статус, безопасные diagnostic metadata
и предупреждения; текст стенограммы, аудио и credentials в него не входят.
`PipelineCheckpointStore` атомарно сохраняет только производные результаты
alignment/diarization под media root и повторно использует их при совпадении
provenance fingerprint. PostgreSQL job остаётся источником истины; повреждённый
checkpoint игнорируется и пересчитывается, а canonical audio/V1 не изменяются.
Подробные правила измерения и безопасной оптимизации описаны в
[whisperx-pipeline-performance.md](whisperx-pipeline-performance.md).
`runtime.py` — единственная совместимая граница с legacy
`app.transcription_pipeline`: он создаёт конфигурацию/контекст, удерживает
resident pipeline и безопасно освобождает его после idle/OOM. `processing.py`
содержит orchestration и stage contracts, но не импортирует legacy напрямую.
`metrics.py` собирает request-scoped `queue_wait_ms`, `media_prepare_ms`,
`model_load_ms`, `normalize_ms`, `asr_ms`, `alignment_ms`, `diarization_ms`,
`postprocess_ms`, `total_processing_ms`, `audio_duration_ms`, RTF, peak VRAM,
GPU utilization, CUDA OOM и checkpoint hit-state/rate. Media Worker передаёт
`media_prepare_ms` в GPU hand-off. Эти diagnostics попадают в
`pipeline_metrics` metadata/quality без текста, аудио или секретов и не меняют
внешние API/worker contracts. `ASR_BATCH_SIZE` ограничен
`ASR_MAX_BATCH_SIZE` (по умолчанию 16), а `GPU_CONCURRENCY=1` остаётся
обязательным до отдельного VRAM soak-test.
`workers/ml_worker/speaker_registry.py` — чистая граница сопоставления
диаризационных меток с owner-scoped профилями голоса. Он валидирует embedding,
считает cosine similarity и требует одновременно порог и отрыв от второго
кандидата; при недостаточной уверенности возвращает suggestion/unmatched и не
переименовывает спикера. Профили не содержат аудио, а V1 остаётся неизменной;
mapping записывается только во время enrichment/V2.
Подмодуль `domain/` содержит identifier-only проекции графа
`Meeting → Recording → ProcessingJob → Transcript`; он не импортирует БД,
WhisperX, аудио или секреты. `storage.py` является единой границей разрешения
opaque `/data/...` ключей для media и GPU worker.

## Инфраструктура и эксплуатация

### `compose*.yml` и `scripts/`

Compose описывает API, PostgreSQL, NATS, tusd и workers. Скрипты запускают,
останавливают, диагностируют, мигрируют и упаковывают runtime. Release Compose
использует immutable image tags одной build identity; `down -v` запрещён.

### `tests/`

Contract tests проверяют содержимое контрактов без микрофона, unit/media tests
проверяют чистые функции, runtime-gates проверяют установленный Windows/LAN
сценарий. Если тест проверяет строковый контракт, рядом должна быть ссылка на
инвариант, который он защищает.

## Общие идентификаторы

```text
voiceTraceId → commandId → localSessionId → meetingId
             → mediaAssetId → asrJobId → transcriptV1/V2 → summary
```

`responseId` используется для TTS-интервала. `queryId` используется для
Assistant delivery/tombstone. Не переиспользуйте один идентификатор для разных
доменов.

## Что нельзя менять без отдельного review

- Recorder IPC v6 и значения локальных состояний.
- Raw-first порядок: PCM/metadata до FLAC, archive и network.
- RBAC и meeting scope Assistant retrieval.
- Сохранность ProgramData, spool, archive и DPAPI при обновлении.
- Единая release identity между Desktop, Recorder, Voice Host, API и workers.
