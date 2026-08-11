# WhisperX Atom

WhisperX Atom — локальная система записи совещаний, доставки аудио, транскрибации WhisperX и последующего анализа. Рабочий пользовательский интерфейс — нативное Windows-приложение WinUI 3. Запись и CUDA выполняются на Windows-хосте, а серверные сервисы работают в Docker.

## Быстрый старт

1. Установите Docker Desktop, .NET 10 SDK, Python 3.12 с рабочим CUDA/WhisperX окружением и FFmpeg.
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

   По умолчанию используется `GPU_WORKER_MODE=host`: PostgreSQL, NATS, API, tusd и серверные workers запускаются в Docker, а WhisperX/CUDA GPU Worker — напрямую на Windows.

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
# Рекомендуемый режим: CUDA и WhisperX на Windows-хосте
.\scripts\run-whisperx.ps1 -GpuMode host

# Резервный режим: GPU Worker в Docker; требует доступного CUDA-образа
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
