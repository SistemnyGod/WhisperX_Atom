# WhisperX.Atom.Desktop module

## Навигация

- `Views/` — страницы и layout UI.
- `ViewModels/` — state, API calls и navigation targets.
- `Models/` — additive client DTOs/display models.
- `Services/` / `Infrastructure/` — API, Recorder, Voice Broker и settings.

## Эксплуатация

Запускать через `scripts/launch-desktop.ps1`, а не второй API/server process.
Переключение вкладок не должно перезапускать запись, Agent или TTS. При
ошибке API показывайте отдельное состояние offline/session expired/Assistant,
но не останавливайте локальный Recorder.
