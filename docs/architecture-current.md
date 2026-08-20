# Текущая архитектура

## Размещение компонентов

```mermaid
flowchart LR
  subgraph Windows[Windows host]
    Desktop[WinUI 3 Desktop\n.NET 10]
    Host[AudioGraph Recorder Host\ncurrent-user]
    Legacy[Recorder Service\nmanual fallback]
    GPU[Host GPU Worker\nPython + WhisperX + CUDA]
    Archive[Durable PCM + FLAC + SQLite spool]
  end
  subgraph Docker[Docker LAN core]
    API[ASP.NET Core API]
    DB[(PostgreSQL)]
    NATS[(NATS JetStream)]
    TUS[tusd]
    Media[Media Worker]
    ContainerGPU[Optional GPU Worker]
  end
  Desktop <-->|Named Pipe v6| Host
  Legacy -.->|manual only| Host
  Host --> Archive
  Host -->|background chunks| API
  Desktop -->|REST / SSE| API
  API --> DB
  API --> NATS
  TUS --> API
  Media --> NATS
  GPU --> NATS
  ContainerGPU -. fallback .-> NATS
```

## Что является текущим runtime

Поддерживаемая конфигурация — Docker core + current-user AudioGraph capture +
host ML. Windows запускает Desktop, AudioGraph Recorder Host из Program Files и
host GPU Worker. Legacy Recorder Service остаётся остановленным ручным fallback.
WhisperX и CUDA загружаются из локального Python окружения; container GPU Worker
остаётся резервным режимом.

## Границы ответственности

### API и workers

API владеет аутентификацией, сессиями, встречами, media metadata, заданиями и
версиями transcript. Media Worker собирает корректный input, GPU Worker выполняет
ASR и enrichment, сохраняя heartbeat и lease в PostgreSQL/NATS.

### Recorder Host

Host снимает выбранный microphone через AudioGraph, преобразует Float32 в mono
PCM16, пишет 30-секундные raw-сегменты и регистрирует их в SQLite. После STOP raw
становится `LOCAL_READY`; отдельный SQLite-driven encoder строит FLAC с retry,
после чего delivery coordinator загружает готовые чанки и выполняет server finalize.
Для ONLINE доступны независимые дорожки `room-microphone` и `system-audio` с общей
временной шкалой; они не смешиваются на границе захвата. Live ASR получает их через
неблокирующий tap, а каноническая V1/V2 всегда строится из исходных дорожек.

### Desktop

Desktop показывает фактические состояния API, Host, raw/encoder/archive/delivery и
processing, отправляет пользовательские команды и подписывается на SSE с polling
fallback. ML-бизнес-логика остаётся в API/worker.

## Источники истины и надёжность

- PostgreSQL — состояние пользователей, meetings, jobs, media, transcript и audit.
- NATS JetStream — доставка событий и redelivery, но не единственное хранилище.
- SQLite spool — локальное состояние Host до подтверждённой доставки.
- Durable PCM/FLAC archive — локальная копия записи; её нельзя удалять при
  временной ошибке API или encoder.
- `artifacts/runtime/state.json` — диагностический снимок, не бизнес-состояние.

## Архитектурный контракт обработки

Серверный ML-код вызывается через единый фасад
[`WhisperXCorePipeline`](../whisperx_atom/core_pipeline.py), а допустимые стадии
проверяются общим контрактом [`pipeline_contract.py`](../whisperx_atom/pipeline_contract.py).
Это совместимый слой над текущим `ProcessingService`: он не меняет IPC/API или
формат существующих stages, но не позволяет worker-модулям напрямую расширять
legacy pipeline несогласованными состояниями. Подробная схема перехода описана
в [architecture-incremental.md](architecture-incremental.md).

Legacy `app.py`, `app/` и watch/runtime-файлы сохранены для совместимости, но не
образуют второй поддерживаемый server pipeline.

## Эксплуатационные safety-gates

- `scripts/start-whisperx-lan-server.ps1` запускает диаризацию только при
  заданном immutable `DIARIZATION_MODEL_REVISION` и валидном `HF_TOKEN`.
  Placeholder, `latest`, `main`, `dev` и `dirty` блокируют запуск до старта
  Docker; это предотвращает непредсказуемую замену gated-модели.
- Desktop проверяет `apiVersion` и `minDesktopVersion` из `/api/system/version`.
  Несовместимый сервер получает явный `API_VERSION_MISMATCH` или
  `VERSION_MISMATCH`, а не маскируется под обычную ошибку readiness.
- `scripts/export-diagnostics.ps1` формирует безопасный ZIP только из
  operational metadata. В него не попадают `.env`, токены, cookies, аудио,
  текст стенограммы и содержимое саммари.
- В Desktop действие «Сформировать диагностический пакет» создаёт такой же
  атомарный `support-bundle-*.zip` в `%ProgramData%\WhisperXAtom\Diagnostics`;
  в отчёт добавляются Recorder/Voice/TTS, очереди и server readiness, но не
  пользовательское содержимое.
- `scripts/e2e-windows-reboot-recovery.ps1` разделяет проверку на ручные фазы
  `BeforeReboot` и `AfterReboot`: штатную перезагрузку выполняет оператор,
  скрипт только фиксирует marker, проверяет Named Pipe v6, Voice Host, Docker,
  server readiness и сохранность `localSessionId`. Автоматическая остановка
  контейнеров и запись аудио этим gate запрещены.
- Отмена встречи сначала в одной транзакции закрывает jobs, outbox и
  `recording_sessions`. Поздний `FinalizeRecordingAsync` повторно проверяет
  статус встречи до idempotent job lookup и возвращает `MEETING_CANCELLED`;
  GPU/Summary persistence также отбрасывают результат отменённой встречи.
