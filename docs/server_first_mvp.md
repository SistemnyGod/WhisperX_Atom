# WhisperX Atom server-first MVP

## Запуск

На Windows 11 с WSL2, Docker Engine и NVIDIA Container Toolkit:

```powershell
docker compose --profile core --profile gpu --profile llm -f compose.dev.yml up -d --build
```

Веб-интерфейс Development: `http://127.0.0.1:8080`

Локальная dev-учётная запись задаётся в Compose:

- username: `admin`
- password: the value of `BOOTSTRAP_ADMIN_PASSWORD` from `.env`

Пароль нужно заменить до любого доступа из LAN. Для production cookie должна работать через HTTPS, а `COOKIE_SECURE` — быть `true`.

## Поток обработки

```text
Vue + Uppy/Tus
  → tusd /data/uploads
  → ASP.NET reservation/completion
  → PostgreSQL outbox
  → outbox-relay
  → NATS JetStream media.ingest
  → media-worker (FFprobe, FLAC, Opus, WAV)
  → NATS ml.transcribe
  → один GPU-worker (WhisperX, alignment, diarization)
  → PostgreSQL transcript/speakers/segments
  → Vue SSE/status/history
```

Медиа-байты не передаются через NATS. Worker-контракты содержат только идентификаторы и storage keys.

## Core E2E smoke

После запуска core-профиля можно выполнить один из сценариев: логин и создание
совещания, tus-загрузку с автоматическим hook или hot-folder импорт. Сам файл
передаётся параметром, поэтому тест не хранит медиа в Git:

```powershell
.\scripts\e2e-core.ps1 -AudioPath .\sample\meeting.flac
.\scripts\e2e-core.ps1 -InboxPath .\sample\meeting.flac
```

Скрипт ожидает `READY`/`FAILED`, получает transcript и завершает работу с
ненулевым кодом при ошибке. Для локального HTTP dev-профиля используется
`COOKIE_SECURE=false`; в HTTPS deployment это значение должно быть `true`.

## Проверки

```powershell
dotnet build apps/server/WhisperX.Atom.Api/WhisperX.Atom.Api.csproj
py -3.14 -m unittest tests.test_server_first_contracts -v
py -3.14 -m compileall -q whisperx_atom workers
docker compose -f compose.dev.yml config
```

Полный legacy desktop-тест требует Python 3.12 и desktop-зависимости
(`customtkinter`, `sounddevice`); server/worker-контракты от них не зависят.

## Ограничения первой версии

- только аудио: WAV, FLAC, MP3, M4A/AAC, OGG/Opus;
- максимум 8 GiB и 4 часа по умолчанию;
- один GPU worker и одна ML-задача одновременно;
- Qwen, Recorder Agent, видео, RTSP и Teams/Zoom импортируются после стабилизации;
- tusd completion выполняется автоматически через защищённый HTTP hook; Web-клиент не получает hook secret.


## Следующий стабильный E2E-контур

- tusd завершает загрузку через защищённый `POST /api/internal/tusd/hooks`; браузер не вызывает completion напрямую.
- `workers/import_worker` сканирует `/data/inbox`, ждёт два стабильных прохода, проверяет аудио через FFprobe, считает SHA-256 и регистрирует один job через `POST /api/internal/imports`.
- Windows-хост монтирует `C:\WhisperXAtom\Data`, `Inbox` и `Archive` в общий `/data`; оригинал остаётся доступным до успешной подготовки производных.
- API отдаёт `/api/meetings/{id}/media` и Range-enabled `/api/media/{id}/preview`; Web подписывается на SSE и использует display name спикеров.
- NATS consumers используют `inbox_messages`, а jobs сохраняют worker/lease/heartbeat/error fields.



Core-only validation:
docker compose --profile core -f compose.dev.yml up -d --build

Full GPU validation:
docker compose --profile core --profile gpu --profile llm -f compose.dev.yml up -d --build
