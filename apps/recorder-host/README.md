# Recorder Host

## Назначение

Current-user процесс, владеющий AudioGraph/WASAPI capture, локальным spool и
durable записью. Сервер недоступен — запись всё равно должна продолжаться.

## Навигация

- `Program.cs` — pipe host и lifecycle.
- `AudioGraphCaptureEngine.cs` — production AudioGraph capture.
- `HostCaptureSource.cs` — выбор capture engine и native format.
- `LiveAudioBroadcaster.cs` — bounded derivative для Voice/monitoring.
- `RecorderHostPipeSecurity.cs` / `RecorderHostProcessGuard.cs` — безопасность.
- `HostDeviceHealthSnapshot.cs` — telemetry устройства без PCM.

## Эксплуатация

Recorder Host запускается Desktop и принимает команды только через защищённый
named pipe. Канонический master не проходит DSP, downmix или resampling;
производные Voice/ASR могут быть отброшены без потери master. При device loss
публикуется одно состояние ошибки, затем выполняется controlled recovery.

Проверки:

```powershell
dotnet build apps/recorder-host/WhisperX.Atom.Recorder.Host.csproj -c Release
dotnet test tests/WhisperX.Atom.Recorder.Tests/WhisperX.Atom.Recorder.Tests.csproj -c Release --no-restore
```

Для диагностики используйте Settings → «Мифодий»/«Диагностика» и Room Check.
Не удаляйте SQLite spool, архив или `archive.flac` вручную во время записи.
