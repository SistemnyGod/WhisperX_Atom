# WhisperX Atom LAN Server

## Canonical runtime

The only supported server runtime is the Compose project `whisperx-atom`:

```text
compose.dev.yml + compose.lan.yml
origin: http://192.168.2.194:8080
GPU_WORKER_MODE=container
AUTO_SUMMARY_ENABLED=false
WHISPERX_MODEL=large-v3
COMPUTE_TYPE=int8_float16
BATCH_SIZE=2
```

Development uses `compose.dev.yml` alone and binds only to `127.0.0.1`; it is for local tests, not the LAN server. Production uses the separate HTTPS profile in `compose.prod.yml`.

Start the LAN server:

```powershell
.\run_whisperx_lan_server.bat
```

The launcher uses `.env.lan`, forces project name `whisperx-atom`, preserves volumes and stops only preserved legacy `whisperx-atom-lan-*` containers. It never runs `down -v`, deletes containers, or clears PostgreSQL/NATS. On startup it recovers durable `QUEUED` work and expired `RUNNING` leases within the attempt limit; `FAILED`, `CANCELLED`, and `READY` records stay terminal.

Stop without deleting data:

```powershell
.\scripts\stop-whisperx-lan-server.ps1
```

Run diagnostics:

```powershell
.\scripts\doctor-whisperx-lan-server.ps1
```

The Desktop client is separate:

```powershell
.\run_app.bat
```

## Первый вход Desktop

Для изолированной LAN-установки пароль администратора задаётся один раз
локальным скриптом. Скрипт принимает `PSCredential`, проверяет наличие ровно
одной учётной записи `admin`, сохраняет только PBKDF2-SHA256 hash и отзывает
старые сессии. Пароль не передаётся в командной строке и не записывается в
репозиторий, `.env.lan` или логи:

```powershell
$credential = Get-Credential -UserName admin
.\scripts\set-lan-admin-password.ps1 -Credential $credential
```

После запуска `run_app.bat` открывает отдельное окно входа. Успешный вход
проверяет Recorder Service, выполняет идемпотентный Agent bootstrap и передаёт
новый Agent token в Recorder только один раз. Существующий Agent token не
ротируется. Cookie-сессия хранится через DPAPI CurrentUser, пароль не
сохраняется.

Если сервер временно недоступен, первый запуск без подтверждённого bootstrap
остаётся заблокирован. После одного успешного online bootstrap Desktop может
создавать local-first ROOM-записи с неизменяемым `OwnerUserId`; доставка будет
повторена после восстановления LAN. Если Recorder Service не запущен, вход
разрешён, но запись остаётся недоступной до запуска службы через UAC.

Qwen remains disabled until the 60-second and 5-minute transcript gates pass. The
Summary Worker, Qwen3-8B runtime and download script are present, but the feature
is deliberately off while `AUTO_SUMMARY_ENABLED=false`. To enable it after the
gate, first verify/create the pinned model manifest and then start explicitly:

```powershell
.\scripts\llm-download.ps1
.\scripts\start-whisperx-lan-server.ps1 -EnableQwen
```

The launcher starts `summary-worker` and explicitly enables automatic summary
jobs only with `-EnableQwen`; it does not silently turn on a GPU workload. The
current model is `Qwen3-8B-Q5_K_M.gguf` and its
manifest must match the pinned SHA256 before the worker reports `READY`.

The launcher writes non-sensitive evidence to `artifacts/acceptance/lan-server/`:
`compose-config.txt`, `core-readiness.json`, and `processing-readiness.json`.
The doctor additionally writes `doctor.json`. Without credentials, the doctor
reports authenticated worker readiness as `AUTH_REQUIRED`; this does not make a
healthy LAN core fail. Do not pass passwords or tokens on a command line.

Readiness is intentionally split into `SERVER_CORE_READY` (API, PostgreSQL,
NATS, storage and gateway) and `PROCESSING_READY` (workers, fresh heartbeats,
GPU lease/queue state). A GPU worker that is processing is reported as `BUSY`,
not as a failure. Qwen is `DISABLED` while `AUTO_SUMMARY_ENABLED=false`.

The 8 GB GPU LAN preset keeps WhisperX `large-v3`, uses `int8_float16` and
`BATCH_SIZE=2`, and leaves pyannote diarization off until a measured acceptance
run confirms enough VRAM headroom. Alignment remains enabled. This avoids a
silent OOM while preserving the large-v3 ASR quality; diarization can be enabled
later by setting `ENABLE_DIARIZATION=true` and repeating the runtime gate.

When a meeting is ready, the Desktop **Файлы** tab exposes **Скачать аудио**.
The API downloads the permanent archive (or the original asset while derivatives
are still being built) with the same meeting access check as the transcript.

The Recorder installer uses only the verified `ffmpeg.exe`/`ffprobe.exe`
payload staged under `vendor\ffmpeg\win-x64`; use
`scripts\stage-ffmpeg-payload.ps1` before publishing an installer. The build
does not silently take a binary from the build host PATH.

LAN-профиль предназначен для изолированной доверенной сети. Development остаётся loopback HTTP, а Production использует отдельный HTTPS gateway.

## Первый запуск

На серверном ПК скопируйте `.env.lan.example` в `.env.lan`, задайте фактические сильные secrets и проверьте `SERVER_ORIGIN`. Не используйте значения `generate-*`, `replace-with-*`, `password` или `changeme`.

```powershell
Copy-Item .env.lan.example .env.lan
.\scripts\start-whisperx-lan-server.ps1 -ConfigureFirewall -InstallStartupTask
.\scripts\doctor-whisperx-lan-server.ps1
```

Профиль публикует только `http://192.168.2.194:8080` (или адрес из `.env.lan`). API, TUS, PostgreSQL и NATS не имеют host-портов. Gateway направляет `/api/*`, `/health/*`, `/ready` в API и `/files/*` в TUS.

`ALLOW_INSECURE_LAN_HTTP=true` является явным разрешением временного LAN HTTP и действует только при `ASPNETCORE_ENVIRONMENT=Lan`. Production guard не ослабляется: Production требует secure cookies, непустые реальные secrets и TLS gateway.

## Клиент и Agent

Машинная настройка хранится в `C:\ProgramData\WhisperXAtom\client-config.json`:

```json
{"schemaVersion":1,"serverOrigin":"http://192.168.2.194:8080","managed":true}
```

После входа Desktop выполняет `POST /api/agents/bootstrap`. Для нового InstallationId plaintext Agent token возвращается один раз, передаётся в Recorder по защищённому IPC и сохраняется DPAPI LocalMachine. Активный Agent при следующем входе только получает user-link; revoked Agent требует повторного enrollment.

Владелец фиксируется при START и сохраняется в локальной SQLite до capture. При временном отключении сервера локальная запись не удаляется и остаётся retryable.

## Диагностика

```powershell
.\scripts\doctor-whisperx-lan-server.ps1
```

Отчёт сохраняется в `artifacts\acceptance\lan-server\doctor.json`. Команда проверяет Compose-конфигурацию, private origin, secrets без вывода их значений, gateway live/readiness и TCP 8080.

LAN MVP не считается release-ready до проверки второго физического ПК, offline recovery и reboot smoke. Для Production используйте `compose.prod.yml` и `infrastructure/caddy/Caddyfile.prod`.
