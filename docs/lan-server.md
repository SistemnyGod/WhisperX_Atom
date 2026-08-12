# WhisperX Atom LAN Server

LAN-профиль предназначен для изолированной доверенной сети. Development остаётся loopback HTTP, а Production использует отдельный HTTPS gateway.

## Первый запуск

На серверном ПК скопируйте `.env.lan.example` в `.env.lan`, задайте фактические сильные secrets и проверьте `SERVER_ORIGIN`. Не используйте значения `generate-*`, `replace-with-*`, `password` или `changeme`.

```powershell
Copy-Item .env.lan.example .env.lan
.scriptsstart-whisperx-lan-server.ps1 -ConfigureFirewall -InstallStartupTask
.scriptsdoctor-whisperx-lan-server.ps1
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
.scripts\doctor-whisperx-lan-server.ps1
```

Отчёт сохраняется в `artifacts\acceptance\lan-server\doctor.json`. Команда проверяет Compose-конфигурацию, private origin, secrets без вывода их значений, gateway live/readiness и TCP 8080.

LAN MVP не считается release-ready до проверки второго физического ПК, offline recovery и reboot smoke. Для Production используйте `compose.prod.yml` и `infrastructure/caddy/Caddyfile.prod`.
