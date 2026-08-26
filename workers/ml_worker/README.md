# GPU / WhisperX Worker

## Назначение

Выполняет WhisperX V1/V2, alignment, diarization и recovery под единым GPU
lease. Это единственный worker, который владеет CUDA pipeline; LLM/Qwen живёт
в отдельном общем runtime Summary/Assistant.

## Навигация

- `worker.py` — consumer, leases, retries и watchdog.
- `gpu_runtime_coordination.py` / `gpu_lease.py` — ownership/concurrency.
- `recovery.py` / `recording_recovery.py` — crash/OOM recovery.
- `speaker_registry.py` — speaker metadata.
- `healthcheck.py` — image/runtime health.
- `technical_events.py` — технические интервалы без пользовательского текста.

## Эксплуатация

Запускается profile `gpu`. Перед обработкой проверяйте CUDA, model manifest,
pyannote/alignment snapshots и heartbeat. `CUDA_OOM`, worker crash и stale
lease проходят bounded retry/recovery; повторная V1 не создаётся, если V1 уже
зафиксирована. Не запускайте второй GPU worker вручную.

```powershell
python -m workers.ml_worker.worker
```

Release readiness должен подтверждать identity и model SHA; при mismatch
Supervisor действует fail-closed.
