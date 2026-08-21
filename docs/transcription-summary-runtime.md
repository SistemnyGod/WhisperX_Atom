# Транскрибация и автоматическое саммари

## Режимы запуска

`scripts/start-transcription-mvp.ps1` остаётся диагностическим transcript-only
режимом по умолчанию. Он намеренно останавливает `summary-worker` и не создаёт
автоматические summary jobs.

Полный локальный runtime запускается через:

```powershell
.\scripts\run-whisperx.ps1 -GpuMode host -Rebuild
```

Он передаёт `AUTO_SUMMARY_ENABLED=true`, поднимает профиль `llm` и оставляет
`summary-worker` работающим. Для временной проверки только ASR используйте:

```powershell
.\scripts\run-whisperx.ps1 -TranscriptOnly
```

Диагностический `llama-server` из профиля `llm-diagnostic` не используется
для production summary и останавливается перед запуском, чтобы не занимать
GPU параллельно с WhisperX. Summary Worker запускает управляемый CPU/GPU
llama runtime только на время задания и соблюдает общий GPU lease.

## Контракт обработки

Запись сначала получает независимый V1 ASR. Ошибка alignment, diarization или
summary не удаляет и не скрывает V1. Автоматический summary job создаётся
только когда `AUTO_SUMMARY_ENABLED=true` и quality-gate подтверждает V2.

`doctor-whisperx.ps1` теперь отражает фактический статус Qwen из `/api/system/readiness`.
В режиме полного запуска недоступный Summary Worker/model считается ошибкой
готовности; в transcript-only режиме он остаётся необязательным.

## Наблюдаемость одной записи

GPU Worker сохраняет числовые метрики в `recording_sessions.stage_timings`,
привязав их к `pipeline_correlation_id`. Для ASR доступны
`queue_wait_ms`, `media_prepare_ms`, `model_load_ms`, `normalize_ms`,
`asr_ms`, `alignment_ms`, `diarization_ms`, `postprocess_ms`,
`total_processing_ms`, `audio_duration_ms`, `RTF`, `gpu_peak_vram_mb`,
`gpu_utilization`, `cuda_oom_count` и `checkpoint_hit_rate`. Метрики
обогащения хранятся с префиксом `enrichment_`, чтобы V1 не перезаписывался;
саммари добавляет `summary_llm_ms`, `summary_persist_ms` и
`summary_total_ms`. Текст, пути, токены и содержимое вопросов в эту запись
не попадают.

V1 после `READY_FOR_ASR` публикуется сразу, если не задан положительный
`TRANSCRIPTION_START_DELAY_SECONDS`. Desktop показывает отдельные стадии
`SCHEDULED`, `WAITING_FOR_OUTBOX`, `WAITING_FOR_GPU` и `PROCESSING`; длительное
наблюдение переводится в фон без локальной ошибки, а recovery выполняет
серверный watchdog.

Доставка Recorder также оставляет идемпотентные технические события в
`recording_events`: `PIPELINE_LOCAL_READY`, `PIPELINE_FLAC_READY`,
`PIPELINE_BIND_STARTED`, `PIPELINE_BOUND`, `PIPELINE_UPLOAD_STARTED`,
`PIPELINE_UPLOAD_READY`, `PIPELINE_FINALIZE_STARTED`,
`PIPELINE_FINALIZE_ACCEPTED`, `PIPELINE_MEDIA_READY`, а при сбое —
`PIPELINE_DELIVERY_PENDING` или `PIPELINE_DELIVERY_FAILED`. Они не содержат
аудио или текст встречи и передаются существующим batch-каналом после
восстановления сети. Серверные `stageTimings` доступны в pipeline endpoint
вместе с `recordingSessionId → meetingId → mediaAssetId → jobId → V1/V2 →
summary`.
