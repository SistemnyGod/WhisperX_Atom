# Application modules

| Module | README | Runtime role |
| --- | --- | --- |
| Desktop | [apps/desktop](desktop/README.md) | WinUI user interface and broker |
| Recorder Agent | [apps/recorder-agent](recorder-agent/README.md) | local IPC, spool and fallback service |
| Recorder Host | [apps/recorder-host](recorder-host/README.md) | current-user AudioGraph capture |
| Voice | [apps/voice-host](voice-host/README.md) | wake/Vosk, local commands and TTS orchestration |
| TTS | [apps/tts-host](tts-host/README.md) | frozen local Silero JSONL host |
| Server API | [apps/server](server/README.md) | ASP.NET API container boundary |
| Web diagnostics | [apps/web](web/README.md) | optional diagnostic UI |

Запускать Desktop/Recorder/Voice только через штатные launchers и installer.
Эти приложения не запускают второй Docker runtime.
