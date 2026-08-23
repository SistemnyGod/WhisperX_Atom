# WhisperX Atom

## Canonical LAN server

For the shared server runtime use only `compose.dev.yml + compose.lan.yml`
with Compose project `whisperx-atom` and origin
`http://192.168.2.194:8080`. Start it with
`.\scripts\start-whisperx-lan-server.ps1` (or
`run_whisperx_lan_server.bat`). This publishes only the Caddy gateway; API,
PostgreSQL, NATS and tusd stay on the Docker network. Qwen remains disabled
until the transcript gates pass.

`compose.dev.yml` by itself is Development-only and loopback-bound. Production
uses the separate HTTPS profile. `run_app.bat` is the Desktop client launcher;
it does not start a second server.

The LAN launcher does not treat a running Docker container as processing-ready.
Worker containers must report a fresh PostgreSQL heartbeat through their Docker
healthcheck; otherwise startup fails with `LAN_WORKER_HEALTH_FAILED` and the
Desktop must remain in a degraded/offline state until the worker recovers.

WhisperX Atom — локальная система записи совещаний, доставки аудио, транскрибации WhisperX и последующего анализа. Рабочий пользовательский интерфейс — нативное Windows-приложение WinUI 3. Запись и CUDA выполняются на Windows-хосте, а серверные сервисы работают в Docker.

## Быстрый старт

1. Для локальной разработки допустим host GPU; для LAN-релиза GPU Worker запускается только в immutable container image. Установите Docker Desktop, .NET 10 SDK и Python 3.12 с рабочим CUDA/WhisperX окружением. FFmpeg/ffprobe нужны для фонового FLAC/master, но их временная недоступность не блокирует локальную запись.
2. Создайте локальную конфигурацию:

   ```powershell
   Copy-Item .env.example .env
   notepad .env
   ```

   Заполните секреты. Файл `.env` не коммитится.

3. Запустите поддерживаемый runtime:

   ```powershell
   .\scripts\run-whisperx.ps1
   ```

   В Local Development по умолчанию можно использовать `GPU_WORKER_MODE=host` для диагностики. В LAN Release обязательно `GPU_WORKER_MODE=container`: PostgreSQL, NATS, API, tusd и все WhisperX workers, включая CUDA GPU Worker, запускаются из immutable Compose/Bundle.

4. Проверьте состояние без обращения к Docker Registry:

   ```powershell
   .\scripts\doctor-whisperx.ps1 -SkipRegistry
   ```

5. Выполните локальный E2E с реальным аудио:

   ```powershell
   .\scripts\e2e-transcript.ps1 `
     -AudioPath "C:\path\to\meeting.m4a" `
     -Runs 1
   ```

Безопасная остановка:

```powershell
.\scripts\stop-whisperx.ps1
```

Архив, SQLite spool и незавершённые задания при остановке не удаляются. Recorder Service остаётся запущенным, если не передать `-StopRecorder`.

## Режимы GPU

```powershell
# Local Development only: CUDA и WhisperX на Windows-хосте
.\scripts\run-whisperx.ps1 -GpuMode host

# LAN Release: GPU Worker в Docker; требует immutable CUDA-образа
.\scripts\run-whisperx.ps1 -GpuMode container
```

## Документация

- [Индекс документации](docs/README.md)
- [Обзор приложения и пользовательские сценарии](docs/application-overview.md)
- [Текущая архитектура](docs/architecture-current.md)
- [Модули и зоны ответственности](docs/modules.md)
- [Технологический стек](docs/technology-stack.md)
- [Потоки данных](docs/data-flow.md)
- [Настройка](docs/configuration.md)
- [Эксплуатация host-runtime](docs/operations-host-runtime.md)
- [API и IPC](docs/api-and-ipc.md)
- [Тестирование и приёмка](docs/testing.md)

Исторические ADR, планы и WSL2-инструкции сохранены в [docs/](docs/) и отмечены в индексе как справочные материалы.

## Репозиторий

```text
apps/                  .NET API, Desktop, Recorder Agent, Voice Host
workers/               import, media, ML, outbox и summary workers
whisperx_atom/         общие Python-контракты и quality logic
scripts/               запуск, doctor, E2E, watchdog и публикация
docs/                  архитектура, runbook и техническая документация
tests/                 контрактные, unit и integration tests
compose.dev.yml        Docker core и резервный container GPU runtime
```

Legacy `app.py`, `app/` и старые watch/runtime-сценарии не удалены: они используются для совместимости и regression-проверок, но не являются вторым рекомендуемым production pipeline.
