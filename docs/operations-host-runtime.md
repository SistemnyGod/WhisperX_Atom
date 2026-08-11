# Эксплуатация host-runtime

## Запуск

```powershell
.\scripts\run-whisperx.ps1
```

Команда загружает `.env`, проверяет локальные Python/CUDA/WhisperX/FFmpeg, запускает Docker core с `--pull never`, поднимает один host GPU Worker, проверяет readiness, запускает Recorder/Desktop и watchdog. Повторный запуск не должен создавать второй worker или второй Desktop.

Для rebuild образов:

```powershell
.\scripts\run-whisperx.ps1 -Rebuild
```

Для резервного container GPU режима:

```powershell
.\scripts\run-whisperx.ps1 -GpuMode container
```

Только этот режим требует проверки CUDA-образа/Registry. Host режим не зависит от Registry.

## Проверка

```powershell
.\scripts\doctor-whisperx.ps1 -SkipRegistry
```

Сводный снимок сохраняется в `artifacts/runtime/state.json`. Transcript doctor и E2E сохраняют дополнительные отчёты в `artifacts/transcription-mvp/<run>/`. В отчётах нет паролей и токенов.

Ключевые поля runtime state:

- `overall` — `READY`, `DEGRADED` или `FAILED`;
- `runtime` — `host` или `container`;
- `hostGpuWorkerPid`;
- `lastHeartbeat`;
- `currentJobId`;
- `lastErrorCode`;
- `hostVersions` — Python, Torch, CUDA и WhisperX diagnostics;
- `qwen` — `DISABLED` в Transcript MVP.

## Watchdog

```powershell
.\scripts\watch-host-gpu-worker.ps1
```

Watchdog проверяет PID, command line ожидаемого Python и свежесть heartbeat. Номинальные параметры: heartbeat 20 секунд, проверка 10 секунд, stale threshold 75 секунд, до 5 перезапусков за 15 минут и 30 секунд паузы перед новым запуском. При превышении лимита состояние становится `DEGRADED`; очередь, spool и архив сохраняются.

Для установки запуска при входе пользователя используется отдельная команда:

```powershell
.\scripts\install-whisperx-startup-task.ps1
```

Команда не вызывается автоматически.

## Остановка

```powershell
.\scripts\stop-whisperx.ps1
```

Остановка выполняется в порядке watchdog → host GPU Worker → Docker core. По умолчанию Recorder Service не останавливается. `-StopRecorder` останавливает и его. Volumes, archive, SQLite spool и незавершённые jobs не удаляются.

## Диагностические коды

| Код | Значение |
| --- | --- |
| `HOST_PYTHON_NOT_FOUND` | Не найден Python из `WHISPERX_HOST_PYTHON` |
| `CUDA_UNAVAILABLE` | Host CUDA недоступна |
| `WHISPERX_IMPORT_FAILED` | Не импортируется WhisperX |
| `DOCKER_UNAVAILABLE` | Docker Desktop не готов |
| `API_NOT_READY` | API или readiness не готовы |
| `WORKER_HEARTBEAT_STALE` | Нет свежего worker heartbeat |
| `WORKER_RESTART_LIMIT` | Watchdog достиг лимита перезапусков |
| `RECORDER_UNAVAILABLE` | Recorder Service/IPC недоступен |
| `TUS_UPLOAD_FAILED` | Ошибка resumable upload |
| `TRANSCRIPT_NOT_READY` | Transcript не достиг READY/PARTIAL_READY |

## Типовые действия

- `API_NOT_READY`: проверить `docker compose ... ps`, затем `doctor-whisperx.ps1 -SkipRegistry`.
- `WORKER_HEARTBEAT_STALE`: посмотреть `artifacts/runtime/state.json` и `artifacts/runtime/*.log`; watchdog перезапускает только host GPU Worker.
- `CUDA_UNAVAILABLE`: проверить `WHISPERX_HOST_PYTHON`, `python -c "import torch; print(torch.cuda.is_available())"` и драйвер.
- `RECORDER_UNAVAILABLE`: проверить службу `WhisperXAtomRecorder`, Named Pipe `WhisperXAtomAgent` и FFmpeg.
- `TUS_UPLOAD_FAILED`: повторить E2E; клиент продолжит с `Upload-Offset`, а не с нуля.
