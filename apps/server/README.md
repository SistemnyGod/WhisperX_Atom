# Server modules

## Назначение

Каталог серверного API и его Docker-границ. Server Node запускается immutable
Compose/Server Bundle; Desktop не монтирует сюда исходники и не запускает
контейнеры самостоятельно.

## Навигация

- `WhisperX.Atom.Api/` — ASP.NET API, readiness, routing, auth и migrations.
- `../workers/` — import, media, GPU/WhisperX, summary, memory и outbox.
- `../../compose.*.yml` — profiles и сети.
- `../../scripts/start-server-bundle.ps1` — штатный запуск bundle.
- `../../scripts/doctor-server-bundle.ps1` — authenticated readiness и identity.

## Эксплуатация

Для LAN используйте только Server Bundle и `compose.dev.yml + compose.lan.yml`
с проектом `whisperx-atom`. Сначала проверьте `.env.lan`, release manifest,
Docker Engine и Supervisor, затем запускайте bundle. Не меняйте PostgreSQL
вручную и не запускайте второй compose-проект рядом с canonical runtime.

```powershell
pwsh -NoProfile -File scripts/start-server-bundle.ps1 -BundleRoot <bundle> -ConfigRoot C:\ProgramData\WhisperXAtom\Server
pwsh -NoProfile -File scripts/doctor-server-bundle.ps1 -BundleRoot <bundle> -ConfigRoot C:\ProgramData\WhisperXAtom\Server -Mode Quick
```

При identity mismatch Supervisor работает fail-closed; сначала исправьте
`active-bundle.path` и Scheduled Task, а не перезапускайте контейнеры вслепую.
