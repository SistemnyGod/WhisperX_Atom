# WhisperX Atom LAN Server

## Canonical runtime

The only supported server runtime is the Compose project `whisperx-atom`:

```text
compose.dev.yml + compose.lan.yml
origin: http://192.168.2.194:8080
GPU_WORKER_MODE=container
AUTO_SUMMARY_ENABLED=false
```

Development uses `compose.dev.yml` alone and binds only to `127.0.0.1`; it is for local tests, not the LAN server. Production uses the separate HTTPS profile in `compose.prod.yml`.

Start the LAN server:

```powershell
.\run_whisperx_lan_server.bat
```

The launcher uses `.env.lan`, forces project name `whisperx-atom`, preserves volumes and stops only preserved legacy `whisperx-atom-lan-*` containers. It never runs `down -v`, deletes containers, clears PostgreSQL/NATS, or resumes cancelled/failed jobs.

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

Qwen remains disabled until the 60-second and 5-minute transcript gates pass. Enable it only with the explicit `-EnableQwen` launcher option.

The launcher writes non-sensitive evidence to `artifacts/acceptance/lan-server/`:
`compose-config.txt`, `core-readiness.json`, and `processing-readiness.json`.
The doctor additionally writes `doctor.json`. Without credentials, the doctor
reports authenticated worker readiness as `AUTH_REQUIRED`; this does not make a
healthy LAN core fail. Do not pass passwords or tokens on a command line.

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
