# WhisperX Atom Voice Host

`WhisperX.Atom.Voice.Host` is the hidden, Desktop-managed Windows SessionHost. It listens to the selected microphone in memory, recognizes the wake word `Мифодий` (the phonetic alias `Мефодий`; legacy `Атом` is disabled by default and can be explicitly enabled with `WHISPERX_WAKE_COMPAT_ATOM=true`), executes only an explicit allowlist of fixed recorder commands, and forwards every other confident utterance to the conversational Assistant. In managed mode it sends intents to the Desktop broker; Desktop is the only owner of Recorder commands and the Voice Host never owns recordings or spool data.

Until microphone and voice-to-answer acceptance are complete, the feature is
marked **Experimental**. The Desktop Broker, Assistant API and grounded Qwen
path are implemented. The runtime already creates a separate unrestricted Vosk
session for the question after the wake word; only the installed microphone
and voice-to-answer acceptance gate remains a release blocker. See
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

# Far-field matrix (0.5/1/2/3 m × quiet/office/ventilation/conversation)
pwsh -NoProfile -File scripts/e2e-far-field-voice.ps1 -Mode Installed `
  -FixtureRoot "C:\path\far-field-fixtures" `
  -OutputPath artifacts/acceptance/far-field-voice-v1.json
```

The matrix expects explicit replay fixtures named
`<distance>\<condition>.wav`. It reports recall, recognized endpoints and
false activations without writing recorder data or forwarding commands. A
missing fixture, dirty Voice Host identity, timeout or false activation keeps
the gate `BLOCKED`; the contract-only mode writes the matrix description
without opening the microphone.

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
- The Voice Host applies a derived far-field front-end after resampling: a
  speech-band high-pass, bounded adaptive gain and limiter. This copy is used
  only by VAD/Vosk; Recorder durable PCM, FLAC and playable WAV retain the
  original signal. The front-end is allocation-free per frame and resets only
  with the Voice Host audio lifecycle.
- VAD sensitivity and recognition confidence are separate policies. A high
  VAD sensitivity no longer imposes a 0.68 floor on ordinary questions;
  conversational queries use the recognition floor, while START/PAUSE/RESUME
  use a stricter command threshold and STOP remains confirmation-gated below
  0.70.
- `STATUS` exposes the adaptive VAD noise floor and threshold in dBFS. Desktop
  shows these values as an acoustic diagnostic, so a quiet or noisy room can
  be calibrated from measured audio instead of a blind sensitivity guess.
- `CALIBRATION_START`/`CALIBRATION_STOP` provide a short noise-floor sample
  for the Settings button. The accumulator keeps only RMS/peak counters in
  memory, never stores or transmits microphone bytes, and applies the measured
  floor to the VAD policy after completion.
- Bare commands cannot wake the assistant; fixed commands require a wake word.
- After a wake word, the intent gate gives recorder mutations priority only for
  the explicit command allowlist. Any confidently recognized non-empty
  utterance becomes an `VoiceIntent.AssistantQuery` and `ASSISTANT_QUESTION` with
  `requestedMode=AUTO`; the API resolves it to
  `GENERAL_CHAT`, `CURRENT_MEETING`, or isolated `LIVE_MEETING`.
  Empty, low-confidence, non-finite, or unrecognizable recognition remains
  `Unknown` and cannot reach Recorder or the Assistant.
- Voice Host always forwards conversational requests with `requestedMode=AUTO`.
  It does not resolve `GENERAL_CHAT`, `CURRENT_MEETING`, or `LIVE_MEETING`;
  that policy lives in the server `AssistantModeResolver` and is shared with
  the Desktop text Assistant.
- The bundled small model uses a documented phonetic `Мефодий` grammar fallback. Set `ATOM_VOSK_EXACT_WAKE_WORD=true` only with a model that contains the canonical `Мифодий` token; the runtime reports the active mode in health.
- START/PAUSE/RESUME and product markers require confidence >= 0.70. STOP executes immediately at confidence >= 0.75, asks for `Мифодий, подтверждаю` from 0.55 to 0.75, and requests a repeat below 0.55.
- An optional CPU-only `whisper.cpp` `ggml-small` second pass runs in `SHADOW` mode for questions and malformed utterances. It is bounded, requires a configured SHA-256, writes no transcript text, and can never change Recorder routing or add user-visible latency. Configure it with `VOICE_ASR_REFINER_MODE`, `VOICE_ASR_REFINER_MODEL`, `VOICE_ASR_REFINER_EXECUTABLE`, and `VOICE_ASR_REFINER_SHA256`.
- Managed startup validates the installed path, build identity, PID and parent process. `STATUS` remains available while startup is in progress; commands return `VOICE_HOST_NOT_INITIALIZED` until the runtime is ready.
- `TEST_SPEECH` is parse-only and never calls Recorder. The latest microphone telemetry contains RMS, peak, clipping, signal state and effective endpoint; pre-wake audio is discarded.
- Responses use `SpeechResponder` → `TtsEngineRouter` → local Silero
  `v5_5_ru` on CPU by default. The host returns a temporary WAV to the
  `SpeechAudioPlayer`; it never receives model/output paths from the caller.
  After repeated Silero failures the router uses Microsoft Irina, then
  Microsoft Irina Desktop, then another installed `ru-RU` voice. English
  voices and the legacy WAV bundle are not fallbacks. See
  [`apps/tts-host/README.md`](../tts-host/README.md) for the JSONL protocol,
  model staging and non-commercial pilot licensing.
- Voice audio is not persisted by the fixed-command path.
- Missing model, microphone, native runtime, or Recorder IPC places SessionHost in `DEGRADED` instead of crashing.
