# Voice Core

Содержит pure contracts, `VoiceIntentParser`, `VoiceCommandArbiter`, bounded
normalization и refiner comparison. Здесь нет микрофона, TTS, Qwen или прав на
Recorder. Все Recorder intents проходят deterministic fail-closed gates.

Проверяйте unit/contract suite при изменении enum/IPC; существующие enum values
не перенумеровывайте. Вопросы, отрицания, условия и цитаты не должны менять
Recorder.
