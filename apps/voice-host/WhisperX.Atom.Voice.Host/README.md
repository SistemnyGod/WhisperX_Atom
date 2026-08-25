# Voice Host process

Здесь находится current-user microphone loop, wake/Vosk sessions, Desktop
Broker, TTS orchestration и lifecycle. `Program.cs`/host runtime — entrypoint;
pipe security, snapshot и telemetry остаются privacy-safe.

Запускайте только под управлением Desktop. `--self-test` и `--doctor` —
безопасные проверки; dry-run/replay не исполняют Recorder. При падении Refiner
или TtsHost Voice Host возвращается к Vosk/русскому fallback и не оставляет
orphan process.
