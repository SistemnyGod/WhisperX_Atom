# Offline Recording Persistence

## Ответственность модулей

- `AudioGraphCaptureEngine` принимает PCM и передаёт кадры в session writer.
- `AudioGraphSessionWriter` и `RawFinalizerQueue` атомарно закрывают `.pcm` и регистрируют raw chunk в SQLite.
- `SpoolStore` — источник истины для timeline, durable-состояния, playable-очереди и deferred delivery.
- `LocalPlayableAudioWriter` после `STOP` строит WAV/RF64 напрямую из проверенных PCM. FFmpeg для этой ветки не используется.
- `PlayableAudioWorker` выполняет сборку последовательно в фоне; падение worker возвращает `BUILDING` в `PENDING` при следующем старте.
- `LocalArchiveWriter` создаёт FLAC/master в той же папке встречи, используя `LocalMeetingDirectoryResolver`.
- `GlobalRawEncoderWorker` отдельно кодирует FLAC; `DeliveryWakeSignal` только ускоряет polling, но не заменяет SQLite.
- `RecordingDeliveryCoordinator` выполняет bind/upload/finalize и повторяет retryable ошибки независимо от playable WAV.

## Состояния

`local_finalize_state=LOCAL_READY` означает проверенный durable PCM и сохраняет IPC v6-совместимость.
Производный файл имеет отдельное состояние:

```text
PENDING → BUILDING → READY
             ├→ RECOVERY_PENDING
             └→ FAILED
```

Старые записи получают `NOT_REQUIRED`; новые записи начинают с `PENDING`. Raw PCM не удаляется, пока playable WAV (или legacy `NOT_REQUIRED`), FLAC и подтверждённая серверная доставка не прошли retention gate.

## Поток после STOP

```text
STOP
  ├─ capture прекращён и PCM закрыт атомарно
  ├─ local_finalize_state = LOCAL_READY
  ├─ playable worker → audio.wav / microphone.wav / system-audio.wav
  └─ encoder + delivery → FLAC → upload → server finalize
```

`*.wav.part` никогда не показывается пользователю. После `Flush(true)`, проверки размера/таймлайна и atomic rename создаётся `recording.json`. Для ONLINE дорожки сохраняются раздельно и выравниваются общей session-relative шкалой тишиной.

## Восстановление

При старте SQLite переводит незавершённый `BUILDING` в `PENDING`; точечный orphan scan восстанавливает закрытые PCM. Повторный запуск идемпотентен: готовый WAV переиспользуется после проверки размера, а повторные wake-сигналы не создают дубли.

## IPC и UI

Recorder IPC остаётся v6. Новые optional tail-поля `playableAudioState`, `playableAudioPath`, `playableAudioFiles`, `playableAudioError` и `playableAudioCreatedAtUtc` не ломают старые Desktop. Команда `LIST_LOCAL_SESSIONS` позволяет новому Desktop восстановить pending offline-записи после перезапуска.

## Приёмочный gate

`scripts/acceptance-audiograph-local-recording.ps1` после `STOP` отдельно ждёт
`playableAudioState=READY`, проверяет зарегистрированные WAV/RF64-файлы и только
затем принимает локальную часть gate. Параметр `-AllowPendingArchive` отключает
только ожидание FLAC/master; он не отключает проверку пользовательского WAV.
