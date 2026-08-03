# Локальный runbook: Windows 11 + WSL2

## Первичная настройка

1. Установите Docker Desktop с включённым WSL2 backend и NVIDIA Container Toolkit.
2. Создайте каталоги `C:\WhisperXAtom\Data`, `C:\WhisperXAtom\Inbox` и `C:\WhisperXAtom\Archive`.
3. Скопируйте `.env.example` в `.env` и задайте уникальные `POSTGRES_PASSWORD`, `BOOTSTRAP_ADMIN_PASSWORD`, `TUS_HOOK_SECRET`, `IMPORT_WORKER_TOKEN` и действительный `HF_TOKEN`. `.env` не коммитится.
4. Выполните preflight:

```powershell
.\scripts\doctor.ps1
```

Проверка не печатает значения секретов. Для проверки только локального host без Docker Hub используйте `-SkipRegistry`; это не заменяет реальный pull.

## Запуск

```powershell
docker compose --env-file .env -f compose.dev.yml --profile core up -d --build
docker compose --env-file .env -f compose.dev.yml --profile core --profile gpu up -d --build
```

Первую команду используйте для API/Web/media smoke без GPU, вторую — для WhisperX worker. Состояние сервисов: `docker compose -f compose.dev.yml ps`.

## Registry/TLS timeout

Ошибка вида `TLS handshake timeout` или `server did not echo the legacy session ID` возникает до сборки проекта. Проверьте последовательно:

```powershell
docker info | Select-String -Pattern 'Proxy|Registry|No Proxy'
docker manifest inspect nats:2.11-alpine
docker compose -f compose.dev.yml pull
```

Если ошибка повторяется, проверьте proxy/registry mirror в Docker Desktop, перезапустите Docker Desktop и повторите `manifest inspect`. Не меняйте application-код и не подменяйте образы непроверенными registry.

## HF models

`HF_TOKEN` нужен только GPU worker для gated pyannote model. После восстановления registry скачайте модели в cache worker-а, затем проверьте:

```powershell
docker compose --env-file .env -f compose.dev.yml --profile gpu up -d gpu-worker
docker compose -f compose.dev.yml logs -f gpu-worker
```

Токен не передавайте через Web и не записывайте в Dockerfile.

## Core E2E smoke

Для тестовой аудиозаписи:

```powershell
.\scripts\e2e-core.ps1 -AudioPath .\sample\meeting.flac
.\scripts\e2e-core.ps1 -InboxPath .\sample\meeting.flac
```

Smoke-сценарий использует login cookie, tusd hook или hot-folder importer и завершается с ошибкой, если job не становится `READY`.

## Local LLM

The first tested summary runtime is the official `Qwen/Qwen3-8B-GGUF`
`Qwen3-8B-Q5_K_M.gguf` at revision
`7c41481f57cb95916b40956ab2f0b139b296d974`. Its SHA-256 is
`068BAE163FAA96AD48032DAF4E071A6A28FE67D8DCC95367609C2FF165E52738`.
The CUDA 12.x server image is pinned to digest
`sha256:39f4f2c5fd4537f85208f905e70269b5b691fbc6de00fd916c37a60a710b7d52`.
The model is stored outside
Docker images under `C:\WhisperXAtom\Models`.

```powershell
.\scripts\llm-download.ps1
docker compose -f compose.dev.yml --profile llm-diagnostic up -d llama-server
.\scripts\llm-smoke.ps1
```

The development API is bound only to `127.0.0.1:8081`. The server uses a
16K context, Q8 KV cache, Flash Attention, one slot, and disabled reasoning
for deterministic meeting summaries.

On the RTX 5060 Ti 16 GB baseline, the 16K profile leaves more VRAM headroom
for WhisperX/pyannote transitions while retaining roughly 50+ tokens/s in
short Russian JSON smokes. Long transcripts are processed with map/reduce.

The Desktop production profile uses a shared PostgreSQL GPU lease. WhisperX
runs first; summary-worker then starts the pinned llama.cpp subprocess only
while it owns the same lease and terminates it before releasing the GPU. Do not
run the diagnostic `llama-server` at the same time as the production workers.

The diagnostic `llama-server` is available only with the `llm-diagnostic`
profile and is intended for a short isolated smoke. The normal Desktop profile
uses `--profile gpu --profile llm` so the full recording-to-summary pipeline is
automatic and does not require manual container stops.
