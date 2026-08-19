# Справочник модулей WhisperX Atom

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

Входной тракт использует один поток Windows AudioGraph.
`AudioGraphCaptureEngine` владеет callback и sample-clock,
`AudioFrameContinuityValidator` до durable writer отклоняет gaps, overlaps,
смену формата и несовпадение размера PCM, а `AudioGraphSessionWriter` сохраняет
raw PCM без FFmpeg, SQLite-запросов и сети внутри callback. Ошибки capture и
writer сходятся в один идемпотентный путь восстановления Recorder Host, который
определяет `LOCAL_READY`, `RECOVERY_PENDING` или `LOCAL_FAILED` по фактически
сохранённым данным.

### `apps/recorder-agent`

Recorder Core и host-specific orchestration: AudioGraph/WASAPI adapters,
SQLite spool, raw recovery, FLAC encoder, local archive, server binding и
delivery. Offline-first: durable PCM создаётся до сетевых действий.

### `apps/recorder-host`

Current-user AudioGraph Host и IPC endpoint. Владеет реальным capture device,
health snapshot и process guard; не владеет серверными jobs.

### `apps/recorder-agent/WhisperX.Atom.Recorder.Service.csproj`

Legacy Windows Service host. Оставлен для совместимости и диагностики; не
запускается параллельно с current-user Recorder Host.

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

### `workers/import_worker`

Обрабатывает импорт аудио/видео из inbox, создаёт media job через API и
передаёт его в тот же media → ASR → V2 → Summary pipeline. Файл не считается
стенограммой до успешной canonical-проверки.

### `workers/outbox_relay`

Доставляет durable PostgreSQL outbox в NATS JetStream. Повторяет сообщения
идемпотентно и не содержит бизнес-логики распознавания.

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
