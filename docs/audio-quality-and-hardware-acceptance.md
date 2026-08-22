# Audio quality и аппаратная приёмка

## Канонический путь

Recorder Host продолжает сохранять исходный AudioGraph PCM16 48 kHz mono. `AudioQualityAnalyzer` только измеряет сигнал и не применяет AGC, шумоподавление, limiter или другие необратимые фильтры к master-файлу.

Room Check выполняется в две фазы: 3 секунды тишины и 7 секунд обычной речи. Отчёт содержит noise floor, RMS/peak речи, SNR, crest factor, DC offset, native/normalized clipping и continuity. Итоговые градации:

- `GOOD`: clipping до 0,01%, SNR не ниже 20 dB;
- `WARNING`: clipping до 0,1% или SNR 12–20 dB;
- `BAD`: clipping до 1%, SNR ниже 12 dB, DC offset выше 0,03 или подтверждённый dropout;
- `CRITICAL`: clipping выше 1%.

Рекомендация в Desktop должна быть конкретной: снизить Mic Boost, приблизить микрофон или выбрать `LARGE_ROOM`.

## Storage reserve и emergency stop

Перед START проверяется сумма `CaptureReserve + PostProcessingReserve`. Во время записи Host каждые пять секунд оценивает свободное место. При `EMERGENCY` он один раз закрывает текущие raw chunks и вызывает обычный durable STOP с `stopReason=LOW_DISK_EMERGENCY`; PCM не удаляется.

## RAW A/B

`RUN_AUDIO_CAPTURE_AB` и capability `AUDIO_CAPTURE_AB_V1` предназначены только для диагностики и запрещены при активной записи. RAW не является тихим fallback для AudioGraph. По умолчанию диагностические файлы удаляются; `keepAudio=true` сохраняет их в локальном diagnostic-каталоге. Переключать canonical capture можно только после 10–20 реальных пар A/B с лучшими quality/WER.

В отчёте A/B теперь присутствуют отдельные SHA256 `audiograph.wav` и `raw.wav`,
а также quality-метрики обоих путей. AudioGraph PCM собирается только в
ограниченном probe-буфере и не попадает в обычный durable master.

## Hardware runner

```powershell
.\scripts\hardware-release-acceptance.ps1 -NodeRole Client -Scenario audio-quality
.\scripts\hardware-release-acceptance.ps1 -NodeRole Server -Scenario server-doctor-release
.\scripts\hardware-release-acceptance.ps1 -NodeRole Aggregate -Scenario aggregate -RunId <id>
```

Runner не выдаёт `PASSED` без реальных operator/runtime evidence. `-AllowReboot`, `-AllowGpuPressure`, `-AllowTemporaryVhd` и `-AllowLongRun` являются явными safety gates.

## Server Doctor

`doctor-server-bundle.ps1 -Mode Quick` проверяет Compose, Docker, authenticated internal readiness и identity. `-Mode Release` дополнительно требует явные `WHISPERX_MODEL_PATH`/`WHISPERX_MODEL_SHA256` и `DIARIZATION_MODEL_PATH`/`DIARIZATION_MODEL_SHA256`, а также сверяет SHA256 Qwen, ONNX и tokenizer в model volume. Doctor не скачивает модели, не печатает токены и возвращает `SERVER_DOCTOR_READY` только при полном совпадении.
