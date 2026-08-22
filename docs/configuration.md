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
| `WHISPERX_MODEL_REPOSITORY` | Pinned faster-whisper repository | `Systran/faster-whisper-large-v3` |
| `WHISPERX_MODEL_PATH` / `WHISPERX_MODEL_SHA256` | Release Doctor: exact canonical WhisperX model file and expected SHA256 | пусто в шаблоне, обязательно заполнить в Server Bundle |
| `DEVICE` | Torch device | `cuda` |
| `COMPUTE_TYPE` | faster-whisper compute type | `float16` |
| `BATCH_SIZE` | ASR batch size | `2` для 8 GB VRAM |
| `ENABLE_ALIGNMENT` | Word alignment stage | `true` |
| `ENABLE_DIARIZATION` | pyannote speaker diarization | `true` при наличии HF доступа |
| `REQUIRE_CUDA` | Запрет CPU fallback | `true` |
| `TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD` | Совместимость загрузки pyannote | `1` |
| `HF_TOKEN` | Доступ к Hugging Face/diarization | Локальный секрет, можно оставить пустым для degraded |
| `DIARIZATION_MODE` | Политика diarization | `preferred` |
| `DIARIZATION_DEVICE` | Устройство pyannote (`auto`, `cuda`, `cpu`) | `auto` |
| `DIARIZATION_MIN_SPEAKERS` / `DIARIZATION_MAX_SPEAKERS` | Ограничение поиска числа говорящих для типовой встречи | `1` / `8` |
| `DIARIZATION_CPU_FALLBACK` | Повторить диаризацию на CPU после CUDA OOM | `true` |
| `DIARIZATION_RELEASE_ASR_ON_LOW_VRAM` | Освободить ASR/alignment cache перед pyannote при низком VRAM | `true` |
| `DIARIZATION_MIN_FREE_VRAM_MB` | Порог освобождения cache перед pyannote | `2048` |
| `DIARIZATION_MODEL_PATH` / `DIARIZATION_MODEL_SHA256` | Release Doctor: exact pyannote/diarization model snapshot and expected SHA256 | пусто в шаблоне, обязательно заполнить в Server Bundle |
| `AUTO_SUMMARY_ENABLED` | Автоматический запуск Qwen после качественной V2 | `true` для LAN-профиля |
| `ASSISTANT_ENABLED` | Включает единый текстовый/голосовой Assistant-контур | `true` для LAN-профиля |
| `LLM_RESIDENT_ENABLED` | Разрешает общий resident Qwen runtime после получения GPU lease | `true` |
| `LLM_IDLE_UNLOAD_SECONDS` | Автоматическая выгрузка неиспользуемого Qwen | `900` |
| `LLM_WARMUP_ENABLED` / `LLM_WARMUP_IDLE_SECONDS` | Отменяемый низкоприоритетный прогрев Qwen после простоя | `true` / `30` |
| `ASSISTANT_EMBEDDING_PROVIDER` | Hybrid retrieval provider (`onnx`, `auto`, `sentence-transformers`, `hash`) | `onnx` |
| `ASSISTANT_EMBEDDING_ONNX_PATH` | Immutable ONNX snapshot MiniLM (CPU provider only) | `/models/embeddings/paraphrase-multilingual-MiniLM-L12-v2.onnx` |
| `ASSISTANT_EMBEDDING_TOKENIZER_PATH` | Immutable tokenizer snapshot для ONNX | `/models/embeddings/tokenizer.json` |
| `ASSISTANT_EMBEDDING_ONNX_SHA256` / `ASSISTANT_EMBEDDING_TOKENIZER_SHA256` | Ожидаемые SHA256 snapshot-файлов; при несовпадении provider отклоняется | пусто в шаблоне, заполнить в Server Bundle |
| `ASSISTANT_EMBEDDING_REQUIRE_VERIFIED` | В release fail-closed при отсутствии/невалидности подписанного ONNX snapshot | `false` для development, `true` в Server Bundle |
| `ASSISTANT_EMBEDDING_MODEL` | Legacy локальная sentence-transformers модель при явном opt-in | `paraphrase-multilingual-MiniLM-L12-v2` |
| `ASSISTANT_FTS_ANCHOR_LIMIT` | Максимум FTS-якорей до semantic rerank | `64` |
| `ASSISTANT_SEMANTIC_CANDIDATE_LIMIT` | Ограниченный semantic pool после RBAC/scope-фильтра | `512` |
| `ASSISTANT_FINAL_TOP_K` | Число лучших anchors перед расширением соседями | `12` |
| `ASSISTANT_NEIGHBOUR_LIMIT` | Максимум соседних сегментов в evidence-контексте | `36` |
| `ASSISTANT_HYBRID_MIN_SCORE` | Минимальный итоговый score | `0.30` |
| `ASSISTANT_HYBRID_EMBEDDING_MIN` | Минимальный embedding score для реального semantic hit | `0.72` |
| `TRANSCRIPTION_START_DELAY_SECONDS` | Необязательная durable-пауза после сборки аудио перед V1/V2 GPU-обработкой; в production V1 запускается сразу | `0` (`>0` только для явного defer) |
| `SUPERVISOR_HEALTH_TOKEN` | Отдельный секрет Server Node для ограниченного internal readiness Supervisor; не admin-пароль | генерируется при установке Server Node |
| `LLM_HEALTH_PORT` | Отдельный LLM diagnostic port | `18080` |
| `LLM_BASE_URL` | Адрес llama-server для LLM режима | Включается отдельно |
| `WHISPERX_STORAGE_EXPECTED_RECORDING_HOURS` / `WHISPERX_STORAGE_EXPECTED_TRACKS` | Динамический минимальный запас локального диска под восстановимую запись | `2` / `2` |
| `WHISPERX_STORAGE_RESERVE_OVERHEAD_PERCENT` | Запас сверх расчётного PCM-объёма (SQLite, WAV parts, метаданные) | `25` |
| `WHISPERX_STORAGE_POSTPROCESSING_RESERVE_BYTES` | Резерв под WAV/FLAC/temp после завершения захвата | `1073741824` |
| `WHISPERX_STORAGE_EMERGENCY_STOP_FREE_BYTES` | Нижний порог свободного места во время активной записи | `1207959552` |

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
