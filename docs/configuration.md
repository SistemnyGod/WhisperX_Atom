# Настройка

## Создание `.env`

Используйте `.env.example` как шаблон:

```powershell
Copy-Item .env.example .env
```

Все runtime-скрипты загружают `.env` через `scripts/WhisperX.Runtime.ps1`. Значения процесса формируются заново при каждом запуске; устаревшие переменные PowerShell не должны переопределять конфигурацию проекта.

## Основные переменные

| Переменная | Назначение | Значение для текущего host runtime |
| --- | --- | --- |
| `WHISPERX_HOST_PYTHON` | Путь к Python CUDA окружения | Обязателен для host GPU Worker |
| `GPU_WORKER_MODE` | Размещение GPU Worker | `host` по умолчанию |
| `GPU_WORKER_RUNTIME` | Диагностическая метка runtime | `host` |
| `WHISPERX_MODEL` | WhisperX model profile | `large-v3` по шаблону |
| `DEVICE` | Torch device | `cuda` |
| `COMPUTE_TYPE` | faster-whisper compute type | `float16` |
| `REQUIRE_CUDA` | Запрет CPU fallback | `true` |
| `TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD` | Совместимость загрузки pyannote | `1` |
| `HF_TOKEN` | Доступ к Hugging Face/diarization | Локальный секрет, можно оставить пустым для degraded |
| `DIARIZATION_MODE` | Политика diarization | `preferred` |
| `AUTO_SUMMARY_ENABLED` | Автоматический запуск Qwen | `false` для Transcript MVP |
| `LLM_HEALTH_PORT` | Отдельный LLM diagnostic port | `18080` |
| `LLM_BASE_URL` | Адрес llama-server для LLM режима | Включается отдельно |

## Каталоги

| Переменная | Содержимое |
| --- | --- |
| `WHISPERX_DATA_HOST` | Общие данные и staging |
| `WHISPERX_INBOX_HOST` | Inbox для import worker |
| `WHISPERX_ARCHIVE_HOST` | Архив оригинальных записей |
| `WHISPERX_MODELS_HOST` | Локальный cache моделей |
| `ATOM_AGENT_DATA_ROOT` | SQLite spool и локальные Agent данные |
| `ATOM_AGENT_FFMPEG_PATH` | Явный путь к FFmpeg, если его нет в PATH |

## Порты

| Порт | Сервис |
| --- | --- |
| `8080` | ASP.NET API |
| `1080` | tusd |
| `4222` | NATS client |
| `8222` | NATS monitoring |
| `55432` | PostgreSQL host port по шаблону |
| `18080` | LLM health/diagnostic port, не API |

## Секреты

`POSTGRES_PASSWORD`, `BOOTSTRAP_ADMIN_PASSWORD`, `TUS_HOOK_SECRET`, `IMPORT_WORKER_TOKEN`, `AGENT_ENROLLMENT_SECRET`, `VOICE_HOST_TOKEN` и `HF_TOKEN` нельзя помещать в Git, `doctor.json`, `state.json`, screenshots или логи. Для постоянной Desktop-сессии и Agent token используются защищённые локальные хранилища согласно текущей реализации.
