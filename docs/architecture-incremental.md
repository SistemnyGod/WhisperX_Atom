# Архитектурная эволюция WhisperX Atom

Документ фиксирует безопасный переход от текущего работающего runtime к
модульной архитектуре «совещание → запись → job → transcript → summary →
assistant». Он является дополнением к [текущей архитектуре](architecture-current.md)
и не отменяет Recorder IPC v6.

## Принятые границы

В продукте остаются четыре устойчивые границы процессов:

```text
Windows Desktop + Voice Host
        │ REST/SSE + Named Pipe v6
        ▼
ASP.NET Core API (модульный монолит)
        │ PostgreSQL + durable outbox/NATS
        ├── Media Worker
        ├── GPU Worker (единственный владелец WhisperX/CUDA)
        └── Summary/Assistant Worker
```

FastAPI, новый набор микросервисов и второй ML-пайплайн не добавляются. Старые
`app.py`, watcher и Web-прототип остаются deprecated-совместимостью и не
получают новых бизнес-функций. Их очереди ограничены, а прерванные после
рестарта задания не ретраятся автоматически.

## Единый Core Pipeline

Все серверные ASR/enrichment вызовы проходят через
[`WhisperXCorePipeline`](../whisperx_atom/core_pipeline.py). Это фасад над
проверенным [`ProcessingService`](../whisperx_atom/processing.py): сейчас он
делегирует существующей реализации, но исключает прямые импорты legacy pipeline
из worker-кода. Следующие этапы (preprocessing, ASR, alignment, diarization и
postprocessing) извлекаются за этим контрактом по одному.

```text
ProcessingRequest
    → CorePipeline.process()
    → normalize / ASR / alignment / diarization / quality
    → ProcessingResult + stage_outcomes
```

`ProcessingRequest` не содержит токенов или произвольных путей. Бинарные данные
остаются в media storage, а job содержит только идентификаторы, состояние и
lease-метаданные.

ASR вынесен за отдельный [`AsrEngine`](../whisperx_atom/asr_engine.py)
контракт. `WhisperXAsrEngine` пока является совместимым адаптером к
`run_asr_pass` legacy pipeline; `ProcessingService` принимает его через
dependency injection, а `WhisperXCorePipeline` пробрасывает зависимость на
границе приложения. Поэтому замена движка или fake engine для contract tests
не требует изменения worker, API или Recorder.

Подготовка производных файлов изолирована аналогично в
[`PreprocessingEngine`](../whisperx_atom/preprocessing_engine.py): канонический
ASR WAV, diarization WAV и профиль `asr_far_field` делегируются текущим
WhisperX/FFmpeg-фильтрам через `WhisperXPreprocessingEngine`. Это сохраняет
проверенные `AUTO/STANDARD/LARGE_ROOM`, анализ сигнала и TTS-mute, но исключает
прямую зависимость `ProcessingService` от private-методов pipeline. Новый
preprocessor можно подключить через `CorePipeline` без изменения worker/API.

Финальная очистка результата вынесена в такой же
[`PostprocessingEngine`](../whisperx_atom/postprocessing_engine.py). Сейчас
`WhisperXPostprocessingEngine` делегирует существующую glossary-нормализацию,
но `ProcessingService` больше не вызывает private-метод pipeline напрямую.
Это оставляет postprocessing заменяемым и позволяет тестировать его без
загрузки WhisperX/torch.

Alignment и diarization имеют такие же независимые границы в
[`AlignmentEngine`](../whisperx_atom/alignment_engine.py) и
[`DiarizationEngine`](../whisperx_atom/diarization_engine.py). Адаптеры
`WhisperXAlignmentEngine` и `WhisperXDiarizationEngine` делегируют текущим
моделям WhisperX/pyannote, включая retry-профили и quality scoring. Это не
разделяет GPU на дополнительные процессы и не меняет порядок `ASR → alignment
→ diarization`; оно только делает стадии заменяемыми и тестируемыми.

## State machine jobs

[`pipeline_contract.py`](../whisperx_atom/pipeline_contract.py) содержит общую
доменную state machine и проверяет имена стадий на границах worker persistence.
Текущие значения БД сохраняются для обратной совместимости:

| Текущее имя | Доменное значение |
| --- | --- |
| `READY_FOR_ASR` | подготовка завершена, можно запускать ASR |
| `TRANSCRIPT_ENRICH` | `ENRICHING` |
| `RETRY_PENDING` / `MEDIA_RETRY_WAIT` | `RETRY_WAIT` |
| `READY` | `COMPLETED` |
| `ALIGNMENT_PARTIAL` | `ALIGNING` с предупреждением |
| `DIARIZATION_PARTIAL` | `DIARIZING` с предупреждением |

Повтор одинаковой стадии идемпотентен. ASR V1 и enrichment — отдельные durable
jobs: `ASR_READY` завершает ASR-job, но разрешает следующему job перейти в
`ENRICHING`. Неподдерживаемое имя стадии отклоняется до записи в PostgreSQL,
чтобы опечатка не превращалась в «вечную» задачу, которую UI не умеет показать.

## Durable jobs и восстановление

- PostgreSQL — источник истины jobs, transcript versions, meeting и leases.
- Outbox — атомарная граница между изменением job и публикацией сообщения.
- NATS JetStream — доставка с redelivery, но не хранилище состояния.
- Worker при старте возвращает просроченные leases в `QUEUED`.
- Повтор обработки идёт с последнего сохранённого результата: V1 не создаётся
  повторно, enrichment не мутирует V1, summary имеет idempotency-проверку.

Дорогие стадии enrichment дополнительно используют
[`PipelineCheckpointStore`](../whisperx_atom/checkpoint_store.py). Alignment и
diarization сохраняют производный JSON-артефакт атомарно через
`.part → fsync → rename`. Артефакт принимается только при совпадении job,
стадии, SHA256 и provenance fingerprint исходной V1/canonical audio. Повреждение
или несовпадение приводит к детерминированному повтору стадии, а не к падению
job. Checkpoint не является источником истины, не заменяет PostgreSQL job/V1/V2
и может быть безопасно удалён и пересоздан. При изменении алгоритма необходимо
поднять `ENRICHMENT_CHECKPOINT_REVISION`.

## Один хозяин GPU

GPU Worker использует локальный [`GpuScheduler`](../whisperx_atom/gpu_scheduler.py)
и межпроцессный
PostgreSQL advisory lease (`whisperx-atom-gpu-0`). Summary/Assistant используют
тот же lease с более низким приоритетом, поэтому ASR не вытесняется Qwen.
По умолчанию `GPU_CONCURRENCY=1`; значение можно поднять до 2–4 только после
измерения VRAM и явного `GPU_PIPELINE_PARALLEL=true`. Одного изменения
`GPU_CONCURRENCY` недостаточно: текущий resident pipeline не считается
потокобезопасным. Scheduler ограничивает конкуренцию внутри процесса, а
PostgreSQL lease сохраняет защиту между несколькими worker-процессами.
Resident-модель освобождается после idle timeout.

## Meeting-centric модель

`meetingId` остаётся границей доступа и retrieval. Локальный
`recording_session_id` связывается с meeting после binding, но не заменяет его.
У одной встречи могут быть несколько recordings, версий transcript, summary,
решений, поручений и evidence. Raw/FLAC и производные ASR-файлы хранятся в
storage, PostgreSQL хранит их ключи и provenance.

## Media storage boundary

Media Worker и GPU Worker используют один
[`LocalMediaStorage`](../whisperx_atom/storage.py). Он принимает только
opaque-ключи из namespace `/data`, отклоняет traversal и возвращает путь внутри
конкретного mounted root. Поэтому импорт, recorder assembly и ASR не имеют
разных реализаций проверки путей. Файловая реализация остаётся локальной, а
переход на NAS/MinIO потребует реализации того же `MediaStorage`-контракта, а не
изменения jobs или API.

## Meeting-centered domain boundary

[`whisperx_atom/domain`](../whisperx_atom/domain) добавляет лёгкие проекции
`MeetingId`, `RecordingId`, `JobId`, `TranscriptId` и identifier-only ссылки.
Они не заменяют текущие строковые поля PostgreSQL/IPC, а проверяют обязательную
связь `meeting_id` на входе worker. Поэтому сообщение без встречи отбрасывается
как poison message до запуска FFmpeg или GPU. Тексты, аудио, токены и ответы
Qwen не входят в доменные объекты.

## План миграции без флагового переписывания

1. **Контракты (выполнено):** общий Core Pipeline, state machine, проверки
   stage и документация границ.
2. **Извлечение стадий (первый слой выполнен):** ASR, preprocessing, alignment,
   diarization и postprocessing вынесены за независимые контракты с
   совместимыми адаптерами и dependency injection в `CorePipeline`.
   [`StageResult`](../whisperx_atom/stage_result.py) типизирует исход каждой
   стадии, а наружу сохраняет прежний `stage_outcomes` mapping. Durable
   checkpoints alignment/diarization уже подключены к enrichment без изменения
   API/БД; следующий срез — retention этих производных артефактов и перенос
   остальных дорогих стадий только после contract/runtime tests.
3. **Доменная модель (первый срез выполнен):** типизированные
   Meeting/Recording/Job/Transcript projections поверх существующих таблиц,
   без переименования IPC/API.
4. **Storage abstraction (первый срез выполнен):** единый интерфейс media
   storage с текущей файловой реализацией и проверкой ключей; позднее возможна
   NAS/MinIO реализация.
5. **Live/Final:** live-черновик остаётся отдельным статусом и не становится
   канонической V2 до финального pipeline.

Каждый шаг должен оставлять рабочими offline-first запись, серверную доставку,
Transcript V1/V2, Summary и grounded Assistant. Миграции выполняются additive;
удаление volumes, spool, старых аудио и legacy-кода не является частью этого
рефакторинга.

## Диагностика архитектуры

Worker heartbeat и jobs должны содержать только `jobId`, `meetingId`, stage,
progress, attempt, lease и error code. В diagnostics запрещены аудио, полный
текст стенограммы, токены и ответы Qwen. Полное описание Windows-модулей и
записи находится в [module-reference.md](module-reference.md) и
[recording-module-reference.md](recording-module-reference.md).
