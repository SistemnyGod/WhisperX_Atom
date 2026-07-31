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
