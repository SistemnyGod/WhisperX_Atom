# Технологический стек

## Приложения и инфраструктура

| Слой | Технология | Роль |
| --- | --- | --- |
| Desktop | C#, .NET 10, WinUI 3, Windows App SDK 2.3.1, XAML | Нативный Windows UI |
| Desktop MVVM | CommunityToolkit.Mvvm 8.4.2 | ViewModels и observable state |
| API | ASP.NET Core, .NET 10, Npgsql | REST, SSE, auth, storage orchestration |
| Agent | .NET 10 Worker/Windows Service, NAudio 2.2.1 | WASAPI capture, IPC и фоновые delivery jobs |
| Voice Host | .NET 10, Vosk, NAudio | Локальные фиксированные голосовые команды |
| Database | PostgreSQL 17 | Durable state, migrations, quality/evidence metadata |
| Messaging | NATS 2.11 + JetStream | Durable jobs и события |
| Upload | tusd 2.6 | Resumable upload; Desktop отправляет блоки по 16 MiB |
| Media | FFmpeg/FFprobe | Probe, FLAC, normalization, assembly и preview |
| ML runtime | Python 3.12, PyTorch CUDA 12.8 | Host WhisperX runtime |
| ASR | WhisperX 3.7.5, faster-whisper | Распознавание и timestamps |
| Speakers | pyannote.audio 3.3.x | Diarization и speaker turns при наличии HF доступа |
| LLM | llama.cpp + Qwen GGUF | Локальный summary/assistant; включается отдельно |
| Automation | PowerShell 7-compatible scripts | Idempotent start, stop, doctor, E2E и watchdog |

## ML размещение

Рекомендуется использовать уже установленное на Windows CUDA/Python окружение. Скрипты проверяют `WHISPERX_HOST_PYTHON`, импорт WhisperX, `torch.cuda.is_available()`, FFmpeg и heartbeat worker. Registry нужен только при `-GpuMode container`.

`TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD=1` сохраняется для совместимости с используемыми pyannote-чекпоинтами. `LLM_HEALTH_PORT=18080` зарезервирован для отдельного локального диагностического llama-server и не должен смешиваться с API на `8080`.

## Принципы выбора технологий

- ML не переносится в .NET: API управляет заданиями, а Python Worker выполняет inference.
- Не добавляются Elasticsearch, Vector DB, Kubernetes или второй message broker.
- PostgreSQL остаётся источником истины; NATS не используется как постоянная БД.
- Qwen не запускается параллельно с WhisperX на одном GPU без существующего GPU lease.
- Модели не заменяются в рамках runtime-стабилизации без отдельного benchmark.
