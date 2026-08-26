# Производительность WhisperX pipeline

Документ фиксирует безопасную границу оптимизации: сначала сохраняется
стабильность V1/V2, затем анализируются фактические timings. Внешние API,
NATS-сообщения и Recorder IPC v6 от этих метрик не зависят.

## Поток выполнения

```text
GpuScheduler (1) → PostgreSQL GPU lease → CorePipeline
  → ProcessingService
  → normalize → ASR (primary/fallback) → alignment
  → diarization → postprocess → persistence
```

`whisperx_atom/runtime.py` владеет resident `TranscriptionPipeline` и
request-context. Это единственная совместимая точка, где разрешён импорт
`app.transcription_pipeline`. При совпадении fingerprint модель остаётся в
памяти; после idle или реального CUDA OOM runtime освобождает её. Один GPU не
запускает два `large-v3` задания: `GPU_CONCURRENCY=1` сохраняется.

`whisperx_atom/processing.py` только оркестрирует stage-интерфейсы:
`PreprocessingEngine`, `AsrEngine`, `AlignmentEngine`, `DiarizationEngine` и
`PostprocessingEngine`. Перед вызовом stage runtime создаёт
`LegacyPipelineStageAdapter`: orchestration видит только операции
`prepare_asr_input`, `transcribe`, `align`, `diarize` и `postprocess`, а
перевод к текущим legacy/private-методам находится в одном месте —
`whisperx_atom/runtime.py`. Прямые вызовы старых методов в stage-адаптерах
оставлены только как fallback для старых тестовых/fake pipeline. Следующий
шаг рефакторинга может заменить любой адаптер независимой реализацией без
изменения worker/API/NATS/DB.

## Метрики

В `metadata.pipeline_metrics` и `quality.pipeline_metrics` сохраняются только
диагностические числа:

- `model_load_ms` — фактическая загрузка lazy ASR/alignment/diarization
  моделей (включая первый resident pipeline); `0` означает reuse;
- `queue_wait_ms` — ожидание локального scheduler и межпроцессного GPU lease;
- `media_prepare_ms` — подготовка canonical ASR derivative в Media Worker,
  переданная в GPU hand-off (для прямых тестовых вызовов — `0`);
- `normalize_ms`, `asr_ms`, `alignment_ms`, `diarization_ms`, `postprocess_ms`;
- `total_processing_ms` (сохраняется также legacy-алиас `total_ms`) и `rtf`
  (wall-clock request time / длительность аудио; алиас `RTF`);
- `audio_duration_ms`;
- `gpu_peak_vram_mb` (legacy `peak_vram_bytes` также сохраняется),
  `gpu_utilization` (best-effort, `null` если NVML/nvidia-smi недоступны),
  `cuda_oom_count`;
- `checkpoint_hit`, `checkpoint_lookups`, `checkpoint_hits`,
  `checkpoint_hit_rate`.

Отсутствие CUDA или checkpoint не является ошибкой обработки: соответствующее
значение остаётся `null`, `0` или `None`. Метрики не содержат аудио, текста,
токенов и абсолютных путей.

## Правила оптимизации

1. Не поднимать `GPU_CONCURRENCY` выше `1` до VRAM/RTF soak-теста.
2. Сначала проверить resident reuse и hit-rate alignment/diarization.
3. Отдельно сравнивать V1 latency и полный V2 latency.
4. `ASR_BATCH_SIZE` настраивать только в bounded диапазоне
   `1..ASR_MAX_BATCH_SIZE` (по умолчанию максимум `16`); `BATCH_SIZE` остаётся
   совместимым alias.
5. Любое изменение stage должно проходить contract/regression suite и
   повторный idempotency/restart gate.
