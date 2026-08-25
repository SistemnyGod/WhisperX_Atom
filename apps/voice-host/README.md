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

# Fixture replay is diagnostic only; it cannot produce production PASSED.
pwsh -NoProfile -File scripts/e2e-far-field-voice.ps1 -Mode Installed `
  -FixtureRoot "C:\path\far-field-fixtures" `
  -OutputPath artifacts/acceptance/far-field-voice-v2.json

# Production evidence: live operator-assisted matrix, privacy-safe metrics only
$identity = (Get-Content artifacts/desktop/build-identity.json | ConvertFrom-Json).buildIdentity
$voiceHost = "C:\path\to\WhisperX.Atom.Voice.Host.exe"
pwsh -NoProfile -File scripts/run-far-field-voice-live.ps1 `
  -VoiceHostPath $voiceHost `
  -VoiceManifestPath "apps/voice-host/Models/Voice/whisper-shadow/voice-refiner.manifest.json" `
  -BuildIdentity $identity -OperatorConfirmed `
  -OutputPath artifacts/acceptance/far-field-voice/live.json
```

The diagnostic matrix expects explicit replay fixtures named
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
- An optional CPU-only resident `whisper.cpp` `ggml-small` second pass runs in `SHADOW` mode for every accepted wake candidate. `VoiceRefinerHost` loads the pinned native bridge/model once and receives memory-only PCM through a current-user named pipe. The queue is bounded to two requests and has no effect on Recorder routing or user-visible latency. Configure it with `VOICE_ASR_REFINER_MODE`, `VOICE_ASR_REFINER_HOST_EXECUTABLE`, `VOICE_ASR_REFINER_MODEL`, `VOICE_ASR_REFINER_SHA256`, `VOICE_ASR_REFINER_NATIVE_LIBRARY`, `VOICE_ASR_REFINER_NATIVE_SHA256`, `VOICE_ASR_REFINER_HOST_TIMEOUT_MS=15000`, `VOICE_ASR_REFINER_CLIENT_TIMEOUT_MS=18000`, and `VOICE_REFINER_THREADS=1..4`. Invalid thread values fail closed to one thread and publish `VOICE_REFINER_THREADS_INVALID`. Missing or unverified assets report `UNAVAILABLE`; they never fall back to a per-utterance CLI or temporary WAV. The host exits with code 73 after a native inference timeout and is recreated for the next Shadow request.
- To stage verified Shadow assets locally, check out whisper.cpp at `f049fff95a089aa9969deb009cdd4892b3e74916` and pass that directory as `-WhisperCppSource` (or `WHISPER_CPP_SOURCE_DIR`) to both native build/staging scripts. Staging also requires the model/native paths, full model and bridge provenance, and the clean build identity. The required multilingual `ggml-small.bin` revision is `c521a4b02f422512d734391fdf08bb08c0862f68` with SHA256 `1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b`. The resulting manifest is schema v2 and is verified by `scripts\verify-voice-refiner-assets.ps1`. Release packaging fails closed unless the manifest, ABI and both SHA256 values verify. Development builds may run only with `VOICE_ASR_REFINER_MODE=OFF` when assets are unavailable.
- Managed startup validates the installed path, build identity, PID and parent process. `STATUS` remains available while startup is in progress; commands return `VOICE_HOST_NOT_INITIALIZED` until the runtime is ready.
- `TEST_SPEECH` is parse-only and never calls Recorder. The latest microphone telemetry contains RMS, peak, clipping, signal state and effective endpoint; pre-wake audio is discarded.
- Responses use `SpeechResponder` → `TtsEngineRouter` → local Silero by default;
  explicit `JARVIS_EN` uses the optional local Piper bridge and falls back
  safely to the Russian path when its assets are unavailable.
  `v5_5_ru` on CPU by default. The default speaker is `eugene` with the
  `MIFODIY_TECH` playback profile; `CLEAN` keeps the unprocessed voice. The
  host returns a temporary WAV to the `SpeechAudioPlayer`; it never receives
  model/output paths from the caller. After Silero/FX failures the router uses
  clean `eugene`, then the installed Russian `ru-RU` Windows fallback. English
  voices and the legacy WAV bundle are not fallbacks. See
  [`apps/tts-host/README.md`](../tts-host/README.md) for the JSONL protocol,
  model staging and non-commercial pilot licensing.
- Voice audio is not persisted by the fixed-command path.
- Missing model, microphone, native runtime, or Recorder IPC places SessionHost in `DEGRADED` instead of crashing.

## Навигация и эксплуатация

`WhisperX.Atom.Voice.Core/` содержит контракты, нормализацию и intent gate;
`WhisperX.Atom.Voice.Host/` — процесс микрофона, wake/Vosk, Desktop Broker и
TTS orchestration; `WhisperX.Atom.Voice.Refiner.Host/` — необязательный resident
Whisper Shadow. Модель Vosk и manifest находятся в `Models/`, а сценарии
приёмки — в `scripts/voice-shadow-corpus.ps1`, `scripts/e2e-far-field-voice.ps1`
и live-run вариантах.

Обычный маршрут: wake-word «Мифодий» → constrained/unrestricted Vosk →
`VoiceCommandArbiter` → локальный Recorder command или Desktop Assistant
Broker. Voice Host не имеет доступа к архиву и не вызывает Qwen напрямую.
Падение Refiner отключает только Shadow/уточнение и оставляет Vosk.

Для локальной эксплуатации используйте `--self-test` и `--doctor`. Команды
`START/PAUSE/RESUME/STOP` проверяются только через deterministic allowlist;
fixture replay запускайте с `--dry-run`, чтобы не менять запись. Production
evidence создаётся live-run и содержит только IDs и метрики.
