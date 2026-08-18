# WhisperX Atom Voice Host

`WhisperX.Atom.Voice.Host` is the hidden, Desktop-managed Windows SessionHost. It listens to the selected microphone in memory, recognizes the wake word `Мифодий` (aliases `Мефодий` and temporary `Атом`) and an explicit allowlist of fixed recorder commands. In managed mode it sends intents to the Desktop broker; Desktop is the only owner of Recorder commands and the Voice Host never owns recordings or spool data.

Until microphone and voice-to-answer acceptance are complete, the feature is
marked **Experimental**. The Desktop Broker, Assistant API and grounded Qwen
path are implemented, but unrestricted Vosk capture for arbitrary spoken
questions is still a release blocker. See
[`docs/mifodiy-current-state.md`](../../docs/mifodiy-current-state.md).

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

The preparation script rejects ZIP path traversal, validates the model layout,
performs a real native Vosk load smoke, and pins the archive plus every
extracted file in `vendor/voice-models/voice-models.lock.json`. Subsequent
publications require exact SHA-256 and size matches. The installer contains
Vosk. Legacy pre-generated WAV responses are not published; responses use an
installed Russian Windows voice.

## Runtime behavior

- Audio callback only copies into a bounded pooled queue.
- One worker performs stateful WDL resampling and feeds Vosk 20 ms PCM16 frames.
- Bare commands cannot wake the assistant; fixed commands require a wake word.
- The bundled small model uses a documented phonetic `Мефодий` grammar fallback. Set `ATOM_VOSK_EXACT_WAKE_WORD=true` only with a model that contains the canonical `Мифодий` token; the runtime reports the active mode in health.
- Stop executes immediately at confidence >= 0.70; otherwise it requires a separate `Мифодий, подтверждаю` within 10 seconds.
- Managed startup validates the installed path, build identity, PID and parent process. `STATUS` remains available while startup is in progress; commands return `VOICE_HOST_NOT_INITIALIZED` until the runtime is ready.
- `TEST_SPEECH` is parse-only and never calls Recorder. The latest microphone telemetry contains RMS, peak, clipping, signal state and effective endpoint; pre-wake audio is discarded.
- Every response uses live Windows TTS. The default is Microsoft Irina, then
  Microsoft Irina Desktop, then another installed `ru-RU` voice. English
  voices and the legacy WAV bundle are not fallbacks.
- Voice audio is not persisted by the fixed-command path.
- Missing model, microphone, native runtime, or Recorder IPC places SessionHost in `DEGRADED` instead of crashing.
