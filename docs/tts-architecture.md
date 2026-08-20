# TTS architecture

The local speech path is intentionally split into four responsibilities:

1. `SpeechResponder` owns the bounded queue (8 items), cancellation
   generation, `IsBusy/IsSpeaking`, and response lifecycle callbacks.
2. `TtsEngineRouter` selects Silero as the primary engine and the installed
   Russian Windows voice as a fallback. It tracks model readiness, fallback
   reason, restart count and synthesis timing.
3. `TtsHost` (`apps/tts-host`) is a persistent frozen CPU process. It loads
   `v5_5_ru` once and communicates with Voice Host through UTF-8 JSON Lines.
   Requests cannot provide model paths, output paths, commands or Python code.
4. `SpeechAudioPlayer` is the only layer that opens WAV output. Volume is
   applied here, and `PlaybackStarted` is raised immediately before playback;
   technical `SYSTEM_RESPONSE_STARTED` therefore never covers synthesis or
   queue wait.

The original PCM/FLAC and meeting data are not changed. Generated WAV files
live under `%LocalAppData%\WhisperXAtom\TTS\Temp` and are removed after
playback, cancellation or failure; stale files older than 24 hours are cleaned
on startup. The model is staged at build time, never downloaded by the target
machine and is currently restricted to an internal non-commercial pilot.
