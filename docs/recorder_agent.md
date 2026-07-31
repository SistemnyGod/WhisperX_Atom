# Windows Recorder Agent — локальный запуск

На текущем этапе Agent уже умеет:

- открыть default microphone через WASAPI;
- открыть WASAPI loopback как отдельную дорожку системного звука;
- писать каждую дорожку в SQLite-backed spool;
- закрывать чанки примерно по 10 секунд;
- конвертировать PCM в FLAC через FFmpeg;
- считать SHA-256 и сохранять метаданные чанка;
- выполнять команды START/PAUSE/RESUME/STOP/STATUS из SSE, если Agent credentials настроены.

Видео, загрузка чанков в server-session и Tray UI подключаются следующими итерациями. Исходные аудиофайлы из `C:\Users\AI_server\Documents\Audacity` автоматически не читаются.

## Предварительные условия

- Windows 10/11;
- .NET 10 SDK/runtime;
- FFmpeg в PATH или путь в `ATOM_AGENT_FFMPEG_PATH`;
- доступные default microphone и default playback device.

## Быстрый smoke

```powershell
$env:ATOM_AGENT_DATA_ROOT = "C:\WhisperXAtom\Agent"
$env:ATOM_AGENT_FFMPEG_PATH = "ffmpeg.exe"
$env:ATOM_AGENT_AUTORECORD_SECONDS = "30"
dotnet run --project .\apps\recorder-agent\WhisperX.Atom.Recorder.Service.csproj
```

После завершения в каталоге `ATOM_AGENT_DATA_ROOT` должны появиться:

```text
agent.db
agent-YYYYMMDD.log
recordings/<session>/<track>/<sequence>.flac
```

Остановить консольный smoke можно `Ctrl+C`. В production Agent будет запускаться как Windows Service.

## Управление из Web

Для command channel задайте URL и credentials уже зарегистрированного Agent:

```powershell
$env:ATOM_AGENT_SERVER_URL = "http://localhost:8000"
$env:ATOM_AGENT_ID = "<agent-guid>"
$env:ATOM_AGENT_TOKEN = "<agent-token>"
```

Web создаёт команды через `/api/meetings/{id}/recording-commands`; Agent читает их через SSE и возвращает результат. При отключённом command channel Agent продолжает локальный spool и smoke-режим.

## Важные ограничения текущего слоя

1. Используются default audio devices текущей Windows-сессии.
2. Формат сохраняется в native WASAPI (на тестовой машине это 48 kHz, 32-bit float, 2 канала); нормализация в mono 16 kHz выполняется серверным media pipeline.
3. `ATOM_AGENT_TOKEN` пока задаётся через окружение; перенос секрета в DPAPI и enrollment из UI — следующий hardening-шаг.
4. При наличии Agent credentials и meetingId Agent создаёт server session/tracks, загружает FLAC-чанки с SHA-256, удаляет локальный файл только после подтверждения и вызывает finalize.
5. Для реальной проверки используйте короткую копию записи или Web upload; большие файлы Audacity остаются ручным пилотным набором.
