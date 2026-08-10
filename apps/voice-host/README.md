# WhisperX Atom Voice Host

`WhisperX.Atom.Voice.Host` is the per-user Windows SessionHost. It captures the default microphone in memory, recognizes only the wake phrase `Атом` and an explicit allowlist of fixed recorder commands, then sends accepted commands to Recorder Service over the protected Named Pipe. It does not own recordings or spool data.

Until microphone acceptance is complete, the feature is marked **Experimental**. Free-form history questions and whisper.cpp are deferred and do not block fixed local commands.

## Local checks

```powershell
dotnet build apps/voice-host/WhisperX.Atom.Voice.Host/WhisperX.Atom.Voice.Host.csproj -c Release
dotnet run --project apps/voice-host/WhisperX.Atom.Voice.Host/WhisperX.Atom.Voice.Host.csproj -c Release -- --self-test
dotnet run --project apps/voice-host/WhisperX.Atom.Voice.Host/WhisperX.Atom.Voice.Host.csproj -c Release -- --doctor
```

Safe acceptance modes never execute Recorder commands:

```powershell
# 50 spoken commands from the default microphone
dotnet run --project apps/voice-host/WhisperX.Atom.Voice.Host/WhisperX.Atom.Voice.Host.csproj -c Release -- --mic-acceptance 50

# Long-recording false-activation check
dotnet run --project apps/voice-host/WhisperX.Atom.Voice.Host/WhisperX.Atom.Voice.Host.csproj -c Release -- --replay "C:\path\meeting.wav" --dry-run
```

## Trusted offline Vosk import

Download the official `vosk-model-small-ru-0.22.zip` manually, keep its original filename, then run:

```powershell
.\scripts\prepare-voice-models.ps1 `
  -VoskZipPath "$env:USERPROFILE\Downloads\vosk-model-small-ru-0.22.zip"
.\scripts\publish-desktop.ps1
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" `
  .\apps\desktop\Installer\WhisperXAtom.iss
```

The preparation script rejects ZIP path traversal, validates the model layout, performs a real native Vosk load smoke, and pins the archive plus every extracted file in `vendor/voice-models/voice-models.lock.json`. Subsequent publications require exact SHA-256 and size matches. The first-stage installer contains Vosk and pre-generated WAV responses; whisper.cpp and GGML assets are not required.

## Runtime behavior

- Audio callback only copies into a bounded pooled queue.
- One worker performs stateful WDL resampling and feeds Vosk 20 ms PCM16 frames.
- Bare commands cannot wake the assistant; fixed commands require `Атом`.
- Stop always requires a separate `Атом, подтверждаю` within 10 seconds.
- Standard responses use preloaded WAV; Windows TTS is reserved for dynamic status and errors.
- Voice audio is not persisted by the fixed-command path.
- Missing model, microphone, native runtime, or Recorder IPC places SessionHost in `DEGRADED` instead of crashing.