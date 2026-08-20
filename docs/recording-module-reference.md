# Контур записи: модули, поток данных и правила изменений

Документ описывает текущую реализацию записи WhisperX Atom. Он является
рабочей картой для сопровождения: здесь зафиксировано, где создаётся аудио,
кто меняет состояние сессии и какие границы нельзя нарушать при рефакторинге.

## Поток данных

```text
Room microphone AudioGraph callback       Render WASAPI loopback callback
  → room-microphone durable PCM             → system-audio durable PCM
  └──────────────────── separate SQLite tracks / FLAC files ─────────────┘
  → SQLite recording_raw_chunks
  → background FLAC encoder
  → recording_chunks / local archive
  → independent delivery
  → API finalize
  → Media Worker
  → Transcript V1 → V2 → Summary
```

Для серверной части эта цепочка дополнительно фиксируется в
`recording_pipeline_runs`:

```text
recording_session_id → meeting_id → media_asset_id → asr_job_id
→ transcript_v1_id → enrichment_job_id → transcript_v2_id
→ summary_job_id → summary_id
```

Lineage не зависит от очереди NATS и используется для восстановления после
перезапуска. Полный безмикрофонный вертикальный gate запускается скриптом
`scripts/e2e-vertical-pipeline.ps1`; он проверяет также отсутствие дубликатов
ASR/enrichment/summary jobs и сохраняет в acceptance JSON только IDs/статусы.

`recording_session_id` — локальный идентификатор. `meeting_id`, `media_asset_id`
и `processing_job_id` появляются после binding с сервером и не заменяют
локальную сессию.

GPU-путь сохраняет отдельные diagnostic metrics в `pipeline_metrics`:
`queue_wait_ms`, `media_prepare_ms`, `model_load_ms`, `normalize_ms`,
`asr_ms`, `alignment_ms`, `diarization_ms`, `postprocess_ms`,
`total_processing_ms`, `audio_duration_ms`, `rtf/RTF`, peak VRAM в bytes/MB,
GPU utilization, CUDA OOM и reuse checkpoint. Эти поля служат для оптимизации
resident `large-v3`; при одной RTX `GPU_CONCURRENCY=1` остаётся неизменным.
Метрики не являются частью canonical audio и не меняют публичные
worker/API-контракты.

## Модули

### `apps/recorder-host/`

`AudioGraphCaptureEngine` открывает выбранный endpoint, принимает Float32
AudioGraph frames и передаёт их в PCM writer. Callback не выполняет SQLite,
FFmpeg, HTTP или тяжёлые операции: он только нормализует кадр и помещает его в
bounded очередь. `SystemAudioCaptureEngine` независимо открывает выбранный
render endpoint через `WasapiLoopbackCapture`, нормализует его в тот же 48 kHz
mono PCM16 и передаёт в отдельный writer. Эти потоки никогда не складываются в
один PCM на клиенте. `RecorderHostRuntime` владеет IPC v6, lifecycle Host и
передачей команд в Core.

Профиль `ONLINE` создаёт обе дорожки (`room-microphone` и `system-audio`),
`SYSTEM_ONLY` — только render-loopback, `ROOM/MIC_ONLY` сохраняют прежний
микрофонный путь. Для фиксированного render endpoint fallback запрещён;
пропавшее устройство даёт `AUDIO_SYSTEM_AUDIO_UNAVAILABLE`. На сервере
`LocalArchiveWriter` видит отдельные track metadata и лишь затем может собрать
master/ASR-производную. Process-specific loopback Teams/Zoom пока использует
выбранный системный render endpoint; изоляция отдельных процессов остаётся
следующим этапом Windows Audio Session API.

Media Worker после STOP создаёт отдельный `track-<track-id>.flac` для каждой
дорожки и записывает `assembly-result.json` с `schema_version=1`. В manifest
для каждой дорожки фиксируются `track_role` (`room_microphone` или
`system_audio`), относительный путь и логический `storage_key`, фактический формат, длительность,
смещение, drift, способ сборки и `selected_for_asr`. Поле
`tracks_are_independent=true` является контрактом: эти файлы сохраняются для
последующей нормализации, диагностики эха и определения удалённых спикеров.
Для профиля `ONLINE` controlled mix создаётся только после успешной сборки и
проверки обеих дорожек; его путь записывается отдельно в `asr_input`, а
исходные track-файлы не заменяются и не помечаются выбранными. В quality
metadata Media Worker переносит тот же список как `independent_tracks`, не
выдавая абсолютные пути хоста.

Перед записью каждого кадра `AudioFrameContinuityValidator` проверяет исходный
sample cursor, размер payload и неизменность формата. Gap, overlap или
повреждённый кадр никогда не склеиваются молча: capture останавливается,
сессия переводится в локальную recovery/finalization и сохраняет точный код
`AUDIO_FRAME_*`. Ошибки AudioGraph и durable writer проходят через единый
failure handler; повторный сигнал той же ошибки подавляется сравнением
активного writer/session.

Изменять безопасно: формат внутреннего кадра и размер очереди при наличии
метрик и тестов. Нельзя менять IPC v6 или запускать сеть/кодировщик из callback.

### `apps/recorder-agent/RecordingCoordinator.cs`

Основной orchestration локальной сессии. START выполняет preflight, создаёт
SQLite-сессию, открывает дорожки и ждёт первый durable frame. STOP закрывает
capture и передаёт локальную финализацию в фоновые координаторы. Автоостановки
по длительности нет; ротация raw-сегментов не является остановкой.

`LegacyRecordingCoordinator` остаётся совместимым адаптером. Новый код должен
использовать `RecordingCoordinatorFacade`, а не обращаться к legacy напрямую.

`PcmFlacChunkWriter` держит текущий `.pcm.part` открытым только до ротации и
периодически делает отдельный durable-checkpoint через
`ATOM_RAW_DURABILITY_CHECKPOINT_SECONDS` (по умолчанию 5 секунд, диапазон
1–60). Checkpoint выполняется в фоне и не удерживает orchestration-lock во
время `fsync`; при гонке с ротацией поздний flush безопасно игнорируется, а
закрытие чанка всё равно выполняет финальный flush. Это ограничивает хвост,
который может потеряться при падении процесса, не добавляя синхронный `fsync`
в AudioGraph callback.

### `apps/recorder-agent/RawChunkFileName.cs` и `RawChunkRecovery.cs`

Имя raw-файла кодирует sequence, start sample и sample count. Recovery после
сбоя атомарно поднимает `.pcm.part`/точный `.pcm`, проверяет размер и
регистрирует отсутствующую SQLite-строку. В памяти и по сети pre-roll не
сохраняется.

### `apps/recorder-agent/SpoolStore.cs`

SQLite — источник истины для сессий, raw/encoded chunks, событий, leases и
retry. `GetLocalDurabilityAsync` перед `LOCAL_READY` сверяет байты на диске,
объединяет raw и уже созданные FLAC-чанки и передаёт timeline в
`RecordingTimelineValidator`. Exact orphan scan ограничен каталогом текущей
сессии; глобальный scan остаётся периодической страховкой.

После raw retention строки `recording_raw_chunks` могут исчезнуть. Поэтому
валидатор обязан видеть `recording_chunks` со статусами `READY`, `UPLOADING` и
`CONFIRMED`; иначе отложенная доставка ошибочно превращает готовую сессию в
`NO_AUDIO_CAPTURED`.

### `apps/recorder-host/StorageRetentionWorker.cs`

Это единственный новый владелец плановой очистки для установленного AudioGraph
Recorder Host. Worker запускает state-driven проход с интервалом
`WHISPERX_RETENTION_INTERVAL_SECONDS` (по умолчанию 300 секунд, допустимо
30–3600), а SQLite остаётся источником истины. Порядок прохода: arm grace для
playable WAV, raw recovery, подтверждённые transport chunks, опциональный
server/local archive retention, playable WAV и только затем известные временные
`.wav.part`/`.flac.part`/`.opus.part`. Никакие `.pcm.part`, `LOCAL_ONLY`, активные
сессии, живые encoding leases или незавершённые uploads не удаляются.

`StorageRetentionMetrics` хранит только счётчики и timestamps последнего прохода:
reclaimed bytes по категориям, число кандидатов, armed sessions и failed runs.
Ошибки отдельных шагов не прерывают проход: следующий шаг продолжает выполняться,
а health IPC отражает накопленный счётчик ошибок.
Тексты встреч, аудио и credentials в метрики не попадают.

### `apps/recorder-agent/RecordingTimelineValidator.cs`

Чистая политика без I/O. Для каждой дорожки проверяет sequence с нуля,
непрерывность `start_sample`, отсутствие gaps/overlaps, единый sample rate,
channels, bits, encoding и track type, а также durable backing каждого чанка.
Коды результата: `SESSION_TIMELINE_GAP`, `SESSION_TIMELINE_OVERLAP`,
`SESSION_TRACK_FORMAT_MISMATCH`, `SESSION_CHUNK_NOT_DURABLE`.
При переполнении диапазона sample используется `SESSION_TIMELINE_OVERFLOW`.

Это предпочтительная точка для новых timeline-инвариантов: добавляйте тесты к
чистой политике, а не в realtime callback или SQL-цикл.

### `apps/recorder-agent/FlacEncoder.cs`, `ExternalProcessRunner.cs`

FLAC/ffprobe выполняются асинхронно вне capture callback. Timeout завершает
дерево процесса и удаляет только незавершённый `.part`; PCM и SQLite-задача
остаются для retry. Числовые поля ffprobe читаются tolerant-парсером (число и
строка).

### `apps/recorder-agent/GlobalRawEncoderWorker.cs`

Поднимает SQLite backlog, получает lease, кодирует один chunk и записывает
health/heartbeat. Очередь bounded, retry и terminal failure независимы от
delivery. При остановке Host незавершённый lease возвращается в backlog.

### `apps/recorder-agent/LocalArchiveWriter.cs`

Собирает проверенные FLAC-чанки в локальный master/archive. Archive retry
отдельный от сетевой доставки. Оригинальные raw/FLAC не изменяются обработкой
ASR; производные файлы создаются на сервере.

### `apps/recorder-agent/RecordingDeliveryCoordinator.cs`

Отправляет только готовые chunks, выполняет binding и idempotent finalize.
Работает независимо от archive и не удаляет transport chunks до server
`CONFIRMED` и purge barrier. Временный offline не блокирует локальную запись.

### `live_runtime.py` (совместимый локальный Tk-путь)

Этот путь не является установленным Recorder Host, но остаётся доступным для
локального live-режима. `SoundDeviceChunkRecorder.record_session` использует
один callback-based `sounddevice.InputStream` на всю сессию. В callback нет
записи на диск: блоки попадают в bounded queue, а отдельный recording-loop
пишет `.wav.part`, делает checkpoint заголовка/`fsync` и атомарно переименовывает
готовый файл. Файлы обработки ротируются независимо от захвата.

Пауза оставляет stream открытым и отбрасывает только входные блоки периода
паузы; media offset рассчитывается по `start_sample / sample_rate`, а не по
wall-clock. Поэтому пауза не создаёт искусственный разрыв в итоговой
стенограмме, а STOP закрывает stream сразу после команды, не ожидая окончания
фиксированного 20-секундного блока. Переполнение bounded queue считается
ошибкой `AUDIO_INPUT_QUEUE_OVERRUN`, а не скрытой потерей аудио.

Voice Host пока имеет отдельный WASAPI-поток для wake-word. Это намеренно не
меняет Recorder IPC v6 в рамках текущего шага: запись не зависит от Voice
Host, а объединение capture fan-out выполняется отдельным контрактным этапом
после runtime-проверки. При активной записи Voice Host не должен открывать
другой endpoint молча или менять выбранный Recorder endpoint.

Для распознавания голоса Voice Host после своего 16 kHz downmix использует
отдельную производную копию `VoiceAudioFrontEnd` (high-pass, ограниченный AGC,
limiter). Эта обработка не применяется к Recorder PCM, playable WAV или FLAC:
оригинал остаётся доступным для восстановления, WhisperX и повторной
обработки. Выбранный endpoint передаётся Desktop из той же настройки
`MicrophoneDeviceId`, что используется Recorder; effective/requested IDs
показываются в health, чтобы расхождение было обнаруживаемым, а не скрытым.

### `apps/recorder-agent/SessionFinalizationCoordinator.cs`

Per-session single-flight. Он не даёт IPC STOP, recovery и delivery одновременно
писать один archive или менять один receipt. Глобальная параллельность между
разными сессиями сохраняется.

## Состояния и границы

```text
WRITING → RAW_READY → ENCODING → READY
                         └──────→ ENCODE_FAILED/TERMINAL_FAILED

PENDING/FINALIZING_LOCAL → LOCAL_READY | RECOVERY_PENDING | LOCAL_FAILED
READY → UPLOADING → CONFIRMED
```

- `LOCAL_READY` означает целостную локальную timeline, а не наличие одной
  SQLite-строки.
- `RECOVERY_PENDING` означает, что `.part`, незавершённая запись или repairable
  orphan ещё могут быть восстановлены.
- `LOCAL_FAILED` используется для противоречивой timeline, повреждённых байтов
  и отсутствия durable audio.
- Ошибка archive не делает capture failed и не останавливает новый START.

После `LOCAL_READY` серверный outbox и workers восстанавливаются независимо:
потерянное событие `media.ingest`, `ml.transcribe` или `llm.summarize` повторно
публикуется relay только для просроченного job без живого lease. Повторная
доставка не создаёт новый media asset или transcript version. ASR callback
сохраняет V1 до запуска enrichment; падение alignment, diarization, HF/
pyannote или CUDA OOM оставляет исходный V1 доступным и переводит только
последующую стадию в retry/`PARTIAL_READY`.

## Правила оптимизации

1. Не делать SQLite/FFmpeg/HTTP в AudioGraph callback.
2. Не читать весь PCM в память: качество и checksum считать блочно.
3. Держать archive и delivery отдельными retry-контурaми.
4. Сначала писать raw и metadata, затем кодировать производные форматы.
5. Любой новый фоновой цикл должен иметь bounded queue, lease/heartbeat и
   отмену при shutdown.
6. Любое изменение состояния должно быть идемпотентным после перезапуска.
7. Для длительной записи использовать media clock из sample count; wall-clock
   допустим только для диагностики latency и heartbeat.
8. Не менять IPC v6 и семантику `LOCAL_READY`, `RECOVERY_PENDING`,
   `LOCAL_FAILED`, `CONFIRMED` без отдельного contract review.
9. Проверять непрерывность входных AudioFrame до append; session-level
   timeline validator после STOP является второй, а не единственной защитой.

## Проверки после изменений

- `dotnet build apps/recorder-host/WhisperX.Atom.Recorder.Host.csproj -c Release`
- `dotnet build apps/recorder-agent/WhisperX.Atom.Recorder.Service.csproj -c Release`
- contract tests `test_recording_*`, `test_offline_recording_recovery_contracts.py`
- synthetic chunks: contiguous, gap, overlap, mixed format, orphan `.pcm`,
  non-empty `.pcm.part`, purged raw with ready/confirmed FLAC.

Для установленного runtime используется `scripts/e2e-recording.ps1`. По
умолчанию он выполняет только локальный baseline и пишет JSON без аудио или
стенограммы. Сценарии `pause-resume`, `server-offline`, `api-crash`,
`workers-restart`, `desktop-close`, `usb-loss`, `low-disk` и `host-crash`
включаются параметром `-Scenario`; Docker/процессы можно останавливать только
с явным `-ExecuteFaults`. Если задан изолированный `-DataRoot`, скрипт не
подключается к уже работающему production Recorder Host. Отчёт содержит
только идентификаторы, состояния, counters, коды ошибок и флаги безопасности.
