# WhisperX Atom TtsHost

`TtsHost` is a local, frozen JSONL process that loads Silero `v5_5_ru` once on
CPU and returns temporary PCM16 WAV files. It accepts only `ping`, `synthesize`
and `shutdown`; model and output paths are owned by the host, not the caller.

The Voice Host owns queueing, cancellation, playback markers and the Windows
fallback. `TtsHost` never receives meeting text in diagnostics and never sends
audio or text over the network. The model is staged at build time and is not
stored in Git. This repository currently targets an internal non-commercial
pilot; commercial release requires a separate license review.
