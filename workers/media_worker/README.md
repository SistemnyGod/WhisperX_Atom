# Media Worker

## Назначение

Собирает доставленные chunks/assets, валидирует media, формирует canonical
archive и ASR derivative, вычисляет audio quality и запускает V1 pipeline.

## Навигация

- `worker.py` — NATS consumer и job lifecycle.
- `media_worker.py` — `prepare_media`, audio analysis и quality gates.
- `recording_assembly.py` — exactly-once assembly/chunk ordering.
- `persistence.py` — durable media state.
- `cli.py` — diagnostic/local entrypoint.

## Эксплуатация

Media Worker запускается Compose profile `core` и получает storage keys, а не
произвольные локальные пути. `WEAK`, `CLIPPING` и недоступная диагностика —
warnings; только повреждённый/пустой ASR input блокирует V1. NumPy должен быть
установлен внутри image, чтобы `analyze_wav()` не превращал диагностику в
ложный `AUDIO_SIGNAL_UNUSABLE`.

Проверяйте job stage, heartbeat и `audio-quality.json`; не переписывайте
archive/ASR вручную. Recovery повторно использует существующие assets.
