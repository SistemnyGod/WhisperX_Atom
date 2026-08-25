# WhisperX Atom TtsHost

`TtsHost` is a local, frozen JSONL process that loads Silero `v5_5_ru` once on
CPU and returns temporary PCM16 WAV files. It accepts only `ping`, `synthesize`
and `shutdown`; model and output paths are owned by the host, not the caller.

The Voice Host owns queueing, cancellation, playback markers and the Windows
fallback. `TtsHost` never receives meeting text in diagnostics and never sends
audio or text over the network. The model is staged at build time and is not
stored in Git. This repository currently targets an internal non-commercial
pilot; commercial release requires a separate license review.

## Навигация и эксплуатация

`tts_host.py` — JSONL process loop, `protocol.py` — allowlisted request/response
контракт, `silero_runtime.py` — загрузка `v5_5_ru`, а `text_normalizer.py` —
безопасная подготовка русского текста. Модель и `model-manifest.json` staged
при сборке; runtime ничего не скачивает.

Voice Host управляет очередью, cancellation и playback. Профиль
`MIFODIY_TECH`/`CLEAN` применяется только после синтеза в потоке
воспроизведения; TtsHost не получает Recorder audio и не публикует текст в
telemetry. При отказе Silero вызывающий Voice Host включает русский Windows
fallback.

Проверка локального протокола выполняется через TtsHost/Voice tests и release
build Desktop. Не запускайте `tts_host.py` с произвольными путями модели или
вывода: эти значения намеренно не принимаются JSONL-протоколом.
