# Resident Voice Refiner Host

Необязательный CPU-only Shadow process: один pinned native whisper.cpp runtime,
named pipe текущего пользователя, concurrency `1` и bounded queue `2`.

Режимы `OFF|SHADOW|ASSISTANT_ONLY|WAKE_AUDIT` задаются снаружи. Refiner не
управляет Recorder и не задерживает локальный ACK; timeout/crash закрывает
только этот Host и восстанавливается на следующем запросе. PCM, Whisper text и
локальные пути не публикуются в telemetry.
