# WhisperX Atom — системное устройство и руководство по работе

Это каноническое объяснение того, как устроен WhisperX Atom в текущей ветке:
какие процессы запускаются на Windows-клиенте, какие — на LAN-сервере, где
хранятся данные, как запись превращается в Transcript V1/V2 и Summary, и как
работает голосовой помощник «Мифодий».

Документ описывает исходный код и поддерживаемые контракты. Фактическая
установленная версия определяется `buildIdentity` из release manifest и может
отставать от исходников. Для runtime-проверки используйте
`scripts/doctor-whisperx-lan-server.ps1`, authenticated readiness и acceptance
скрипты; не делайте вывод о работающем сервере только по этому документу.

## 1. Короткая модель системы

WhisperX Atom состоит из двух машинных контуров.

```text
Windows-клиент пользователя                         LAN-сервер
──────────────────────────                         ──────────
Desktop (WinUI 3)                                  API (ASP.NET Core)
Recorder Host + SQLite + PCM                       PostgreSQL
Voice Host + Vosk                                  NATS JetStream
TtsHost + Silero                                  tusd
Updater                                             Outbox Relay
                                                     Import Worker
                                                     Media Worker
                                                     GPU Worker
                                                     Summary Worker / Qwen
                                                     Caddy LAN Gateway
```

Основной принцип — offline-first запись и durable-first серверная обработка:

```text
START → capture → durable PCM → STOP
                              ├─ локальный WAV/архив
                              └─ фоновой FLAC/upload
                                      → Media
                                      → V1 (ASR)
                                      → V2 (alignment/diarization)
                                      → Summary
```

Сеть, FFmpeg, GPU, Qwen и WhisperX не находятся в realtime-критическом пути
нажатия `STOP`.

## 2. Где что запускается

### Windows-клиент

Устанавливаются и работают под обычным пользователем:

- `WhisperX.Atom.Desktop` — UI, сессия пользователя, ViewModel и Desktop Broker;
- `WhisperX.Atom.Recorder.Host` — current-user AudioGraph/WASAPI capture и IPC;
- `WhisperX.Atom.Voice.Host` — wake word, Vosk, intent gate и TTS orchestration;
- `TtsHost` — локальный Silero `v5_5_ru` по JSONL-протоколу;
- `WhisperX.Atom.Updater` — staged update и rollback.

Клиент не запускает Docker, PostgreSQL, NATS или GPU worker. Закрытие Desktop
штатно завершает Voice Host; Recorder Host сохраняет локальные данные и
восстанавливает их при следующем запуске.

### LAN-сервер

LAN-профиль (`.env.lan`) запускается Docker Compose на отдельном серверном ПК.
Для текущего LAN deployment `GPU_WORKER_MODE=container`; host GPU mode остаётся
отдельным development/diagnostic профилем из `.env.example`.

Персистентные volumes:

- PostgreSQL data;
- NATS data;
- media/data и archive;
- model cache;
- inbox для import;
- release updates и runtime logs.

Patrol360 имеет отдельные контейнеры, volumes и compose project и не входит в
управление WhisperX Supervisor.

## 3. Структура репозитория

```text
apps/
├─ desktop/
│  ├─ WhisperX.Atom.Desktop/   WinUI 3 shell, pages, ViewModels, Broker
│  └─ Updater/                  staged update и rollback
├─ recorder-agent/              Core, SQLite spool, delivery, encoder
├─ recorder-host/               current-user capture process + IPC
├─ voice-host/
│  ├─ WhisperX.Atom.Voice.Core/ parser, contracts, self-tests
│  └─ WhisperX.Atom.Voice.Host/ wake/Vosk/TTS/runtime/pipe
├─ tts-host/                    Python Silero JSONL host и model manifest
└─ server/WhisperX.Atom.Api/    ASP.NET API и SQL migrations

workers/
├─ outbox_relay/                PostgreSQL outbox → NATS
├─ import_worker/               inbox/import → media job
├─ media_worker/                chunks → canonical media/ASR input
├─ ml_worker/                   WhisperX V1/V2
└─ summary_worker/              Summary, Assistant, Qwen, retrieval, grounding

whisperx_atom/                  общие Python stage contracts и engines
compose*.yml                    dev/LAN/release/prod deployment
scripts/                        запуск, supervisor, backup, recovery, bundle, E2E
tests/                          Python contracts, .NET semantic tests, runtime gates
docs/                           архитектура, API/IPC, operations и acceptance
```

## 4. Модули Windows-клиента

### Desktop

`apps/desktop/WhisperX.Atom.Desktop` отвечает за отображение и координацию,
но не содержит ML-бизнес-логики.

| Компонент | Ответственность |
| --- | --- |
| `MainWindow`, `Pages/*` | навигация и экраны Home, Recording, Assistant, Meetings, Settings |
| `ViewModels/*` | состояние UI, polling/SSE, команды пользователя |
| `ServerApiClient` | REST/SSE, cookie session, DTO сервера |
| `BackendService` | интерфейс backend для ViewModel и Broker |
| `RecordingCommandService` | единый путь кнопочных и голосовых START/STOP/PAUSE/RESUME |
| `RecorderPipeService` | Named Pipe v6 к Recorder Host |
| `VoiceHostController` | запуск, health и lifecycle Voice Host |
| `DesktopVoiceBrokerServer` | принимает голосовые события и доставляет Assistant result |
| `AssistantDeliveryStore` | durable ledger opaque `commandId` → `queryId`, чтобы потеря HTTP-ответа, restart/timeout не создавали дубль |
| `ProcessingJobTracker` | наблюдение pipeline без превращения нормального RUNNING в ошибку |
| `ClientRuntimeDiagnostics` | безопасный operational snapshot без токенов и содержимого встреч |

После HTTP `202 Accepted` запрос Assistant считается сохранённым. Foreground
ожидание ограничено; дальнейшее получение результата выполняется в фоне. Если
ответ потерян, Desktop хранит только `commandId` без вопроса/ответа и
выполняет авторизованный lookup; повторный POST с тем же ключом возвращает
исходный `queryId`.

### Recorder Agent и Recorder Host

`Recorder Host` владеет аудиоустройствами и realtime callback. `Recorder Core`
владеет durable state и доставкой.

Поток данных:

```text
AudioGraph microphone ─┐
                       ├→ sample clock → continuity validation → PCM writer
WASAPI system loopback ┘                                      → SQLite spool
```

Независимые дорожки:

- `room-microphone` — микрофон переговорной;
- `system-audio` — loopback/system audio.

Они не смешиваются во время захвата. Для ONLINE-режима сервер получает
раздельные `microphone.wav` и `system-audio.wav` с общей относительной шкалой.

Главные классы:

- `AudioGraphCaptureEngine`, `SystemAudioCaptureEngine` — захват;
- `AudioFrameContinuityValidator` — gap, overlap, format и size validation;
- `SpoolStore` — SQLite очередь сессий, дорожек, chunks и retry;
- `LocalPlayableAudioWriter` — PCM → RIFF/RF64 WAV без FFmpeg;
- `PlayableAudioWorker` — фоновая сборка воспроизводимого файла;
- `DeliveryCoordinator` — bind/upload/finalize с retry и wake signal;
- `AgentPipeHost` — IPC v6 и команды восстановления.

Состояния локального файла:

```text
NOT_REQUIRED       legacy session, playable не нужен
PENDING            PCM durable, WAV ещё не строился
BUILDING           фоновая сборка .wav.part
READY              atomic rename завершён и файл проверен
RECOVERY_PENDING   обнаружен crash/orphan, нужна повторная сборка
FAILED             ошибка playable, PCM сохраняется
```

`local_finalize_state=LOCAL_READY` означает проверенный durable PCM. WAV не
заменяет PCM и не является основанием для раннего удаления исходных данных.

### Recorder IPC v6

Основные команды Named Pipe:

| Команда | Назначение |
| --- | --- |
| `HEALTH` | устройство, storage, Agent/server state, backlog |
| `STATUS` | состояние capture |
| `PREFLIGHT` | проверка захвата до START |
| `START`, `PAUSE`, `RESUME`, `STOP` | управление сессией |
| `LIST_LOCAL_SESSIONS` | восстановление сессий после перезапуска Desktop |
| `GET_SESSION_STATUS` | chunks, delivery и server session |
| `RETRY_UPLOAD` | ручной retry retryable delivery |
| `LIST_AUDIO_DEVICES`, `SELECT_AUDIO_DEVICE`, `TEST_AUDIO_DEVICE` | устройства |
| `MARKER`, `DECISION`, `ACTION_ITEM` | голосовые отметки встречи |

### Voice Host и TtsHost

Voice Host не содержит LLM business logic и не имеет доступа к transcript.

```text
microphone → preprocessing/VAD → wake word «Мифодий»
                         → VoiceIntentParser
                         ├─ strict local command → Desktop/Recorder
                         ├─ Farewell → local Silero response
                         └─ AssistantQuery → Desktop Broker → API
```

Строгие команды никогда не отправляются в Qwen. Инфинитивные или вопросительные
фразы (`«может запустить запись?»`, `«почему остановилась запись?»`) остаются
AssistantQuery.

`Farewell` (`пока`, `до свидания`, `до встречи` и утверждённые варианты)
обрабатывается локально, не требует сервера и не меняет состояние записи.

Три диагностические фразы также являются локальными fast paths:
`«Мифодий, состояние сервера»`, `«Мифодий, состояние обработки»` и
`«Мифодий, свободное место»`. Они читают соответственно API readiness,
processing readiness и Recorder Health; Qwen, retrieval и conversation для них
не запускаются.

Во время TTS режим `VOICE_BARGE_IN_MODE=WAKE_ONLY` слушает только wake word.
После двух уверенных срабатываний текущий ответ отменяется, старый playback
помечается `CANCELLED`, а Voice Host сразу принимает новую фразу. Произвольная
речь без wake word не прерывает ответ; `VOICE_BARGE_IN_MODE=OFF` возвращает
только прежнюю команду «Мифодий, замолчи».

Snapshot Voice Host содержит optional timestamps wake/utterance/Assistant/TTS
и очередные/synthesis/playback timings. Они предназначены для корреляции по
`traceId`, `commandId` и `queryId`; текст вопросов и ответов в telemetry не
сохраняется.

Для серии latency-запросов используется
`scripts/e2e-mifodiy-latency-batch.ps1`. Он опрашивает Assistant каждые 100 мс,
сохраняет только хэши вопросов, ID, статусы и timings, а в summary считает
p50/p95 принятия и полного ответа.

Для локального обезличенного QA-корпуса используется схема
`docs/mifodiy-qa-case.schema.json` и runner `scripts/run-mifodiy-qa.ps1`.
Сначала выполняется безопасная проверка без сервера:

```powershell
.\scripts\run-mifodiy-qa.ps1 -CorpusPath .\qa\mifodiy -ValidateOnly
```

При runtime-прогоне runner принимает только явно заданные meeting IDs и
сохраняет в отчёт хэши вопросов, идентификаторы, режимы, статусы, evidence
count и timings. Тексты вопросов/ответов, стенограммы и персональные данные в
отчёт не попадают; production QA-корпус хранится локально и не входит в Git
или release bundle. Для детерминированного preflight reasoning используется
отдельная синтетическая матрица из 400 кейсов.

TTS-контур:

1. `SpeechResponder` выбирает локальный `TtsEngineRouter`.
2. `SileroTtsEngine` запускает соседний `TtsHost` и модель `v5_5_ru`.
3. Голос по умолчанию — `aidar`, sample rate — 48 kHz.
4. Windows TTS используется только как явно разрешённый fallback при ошибке
   Silero/модели/целостности.
5. Playback получает один `responseId`; отменённый ответ не воспроизводится
   повторно.

### Mifodiy Intelligence v2

После выбора scope через `AssistantModeResolver` worker строит отдельный
`AssistantQueryPlan`: intent, topic, person, требуемые поля, follow-up и
политику ответа. План не является evidence и не может заменить сегменты
стенограммы. Для `RESPONSIBLE`, `DEADLINE`, `DECISION`, `CAUSE`, `TASK`,
`STATUS`, `TIMELINE`, `COMPARISON` и `SUMMARY` retrieval получает собственные
ключи и размер соседнего окна; для причин окно шире, но причинная связь всё
равно должна быть явно произнесена в стенограмме.

Состояние follow-up (`topic`, `intent`, `person`, `dateRange`, последний вопрос)
хранится в `assistant_conversation_state` и используется только для нового
плана retrieval. Ответ Мифодия никогда не становится источником факта.
`transcript_facts` — производный индекс с обязательными
`transcript_version` и `evidence_segment_ids`; при смене версии стенограммы
старые derived facts должны быть инвалидированы и пересчитаны.

## 5. Серверные модули

### ASP.NET API

`apps/server/WhisperX.Atom.Api` владеет REST-контрактом и транзакционными
переходами состояния:

- authentication, sessions и RBAC;
- meetings, recording sessions, tracks и chunks;
- media assets и processing jobs;
- Transcript V1/V2, quality и speaker metadata;
- Summary versions и audit;
- Assistant conversations, queries, evidence snapshots;
- worker heartbeats, readiness, migrations и recovery endpoints.

PostgreSQL — источник истины. API проверяет owner/meeting scope до retrieval и
не позволяет Assistant получить evidence другой встречи.

### Outbox Relay

`workers/outbox_relay` читает durable PostgreSQL outbox и публикует события в
NATS JetStream. Повторная доставка допустима: downstream использует `job_id`,
media id, transcript id и correlation id как idempotency keys.

### Import Worker

`workers/import_worker` сканирует inbox, проверяет стабильность файла и создаёт
media job. Импорт не считается транскрипцией до canonical media validation.

### Media Worker

`workers/media_worker`:

1. читает подтверждённые chunks;
2. проверяет sequence/sample timeline и формат;
3. собирает дорожки и проверяет длительность результата;
4. при drift/truncated stream-copy использует re-encode fallback;
5. создаёт canonical ASR input и передаёт `TRANSCRIBE_ASR` через outbox.

Оригинальная локальная запись не изменяется.

### GPU Worker / WhisperX

`workers/ml_worker` выполняет этапы:

```text
VALIDATE → PREPROCESS → TRANSCRIBE_ASR → V1 persist
                                  └→ ALIGNMENT
                                  └→ DIARIZATION
                                  └→ POSTPROCESS → V2 persist
```

V1 сохраняется раньше enrichment. Ошибка alignment/diarization не удаляет V1;
meeting может быть `PARTIAL_READY`.

Модели и engines находятся в `whisperx_atom/`:

- `preprocessing_engine.py` — подготовка ASR input;
- `asr_engine.py` — WhisperX/faster-whisper;
- `alignment_engine.py` — word alignment;
- `diarization_engine.py` — pyannote policy и speaker segments;
- `postprocessing_engine.py` — glossary, technical events и normalization;
- `pipeline_contract.py` — допустимые stage transitions;
- `runtime.py` и `processing.py` — orchestration/compatibility boundary;
- `metrics.py` — stage timings, RTF, VRAM и checkpoint hit state.

### GPU coordination

На одной RTX 5060 Ti действует один GPU lease:

```text
V1 ASR           priority 10
Assistant        priority 30
V2 enrichment    priority 50
Summary          priority 100
```

Низшее число имеет больший приоритет. Активная LLM generation не прерывается.
ASR может попросить выгрузить только idle resident Qwen; V2 не вытесняет
Assistant. При занятом GPU Assistant остаётся в durable queue со статусом
ожидания, а не теряется и не получает ложный LAN timeout.

### Summary Worker и Мифодий

`workers/summary_worker` содержит два логических потребителя одного runtime:

- Summary — V2 → meeting protocol (`topics`, решения, поручения, риски,
  evidence);
- Assistant — вопрос → routing → retrieval → Qwen → grounding → answer/TTS.

`AssistantModeResolver` выбирает режим `AUTO`:

```text
follow-up scope
  → LIVE_MEETING при сильном live evidence
  → CURRENT_MEETING при сильном current evidence
  → MEETING_MEMORY для history-like вопроса
  → GENERAL_CHAT как безопасный fallback (без meeting evidence)
```

Активная запись сама по себе не превращает каждый вопрос в `LIVE_MEETING`.
Assistant response не является evidence: факты берутся только из transcript/live
segments/meeting metadata. Для meeting modes grounding строго проверяет scope,
числа, даты, имена, evidence IDs и claims. При отсутствии сильных anchors
meeting-запрос завершается без Qwen; `GENERAL_CHAT` может отвечать без evidence.

## 6. Полные пользовательские потоки

### Запись без сервера

```text
Desktop/голос START
  → Recorder Pipe v6
  → capture callback
  → durable PCM + SQLite metadata
  → STOP
  → local_finalize_state=LOCAL_READY
  → PlayableAudioWorker строит WAV в фоне
```

Если сервер выключен, локальный WAV и PCM остаются доступными. После перезапуска
Desktop используется `LIST_LOCAL_SESSIONS`; прежний `sessionId` не требуется.

### Доставка после reconnect

```text
SERVER_OFFLINE
  → PENDING_SERVER / retryable
  → SERVER_CONNECTED
  → MakeRetryableDeliveriesDueAsync
  → DeliveryWakeSignal
  → bind track (idempotent)
  → upload chunks (resumable)
  → finalize
```

Terminal ошибки (`CANCELLED`, auth rejection, owner mismatch, manual repair) не
переводятся в due автоматически.

### Транскрибация и саммари

```text
confirmed chunks
  → Media Worker
  → canonical media
  → READY_FOR_ASR
  → V1 ASR eligible immediately (optional explicit defer only when configured)
  → V1 ASR
  → alignment/diarization
  → V2
  → Summary Worker / Qwen
```

`NEEDS_REVIEW` — пригодный для пользователя терминальный результат, а не
основание бесконечно запускать Summary заново.

### Мифодий без записи

```text
«Мифодий, скажи привет»
  → VoiceIntent.AssistantQuery
  → Desktop Broker
  → API AUTO
  → GENERAL_CHAT
  → Qwen
  → grounded/terminal answer
  → Silero TTS
```

При активной записи общий вопрос остаётся `GENERAL_CHAT`; вопрос о текущих
сегментах может стать `LIVE_MEETING` только после retrieval-проверки.

## 7. Хранилища и источники истины

| Хранилище | Что содержит | Нельзя считать единственным источником |
| --- | --- | --- |
| SQLite spool | локальные сессии, tracks, chunks, retry, playable state | server job state |
| Durable PCM/WAV/FLAC | восстановимая локальная запись | transcript или summary |
| PostgreSQL | users, meetings, media, jobs, transcripts, summaries, Assistant, audit | NATS delivery queue |
| NATS JetStream | транспорт событий и redelivery | бизнес-состояние |
| Model cache | WhisperX, pyannote, Qwen, Silero assets | configuration truth |
| `release-manifest.json` | identity, tags, hashes, migrations | live health |
| diagnostics/state JSON | безопасный снимок | пользовательское содержимое |

Raw PCM можно очищать только после playable/legacy gate, проверенного FLAC,
подтверждённой доставки и retention grace.

## 8. API, сети и IPC

| Endpoint/канал | Назначение |
| --- | --- |
| `/health/live` | liveness API |
| `/health/ready` | инфраструктурный PostgreSQL/NATS/storage probe |
| `/api/system/readiness` | authenticated product readiness, workers, GPU, Qwen, queues |
| `/api/system/version` | API/build identity и compatibility |
| `/api/auth/*` | login, refresh, logout, session |
| `/api/meetings/*` | meetings, media, jobs, transcript, summary |
| `/api/assistant/*` | Assistant query, status, evidence и conversation |
| TUS `/files` | resumable upload |
| Recorder Named Pipe v6 | локальный capture/delivery control |
| Voice Pipe | local Voice Host status/commands |

LAN по умолчанию использует API/gateway на `8080`; дополнительные внутренние
порты задаются compose и не должны публиковаться без отдельного review.

## 9. Конфигурация и секреты

- `.env.example` — базовый/dev профиль;
- `.env.lan.example` — шаблон отдельного LAN-сервера;
- `.env.production` — только HTTPS production;
- `.env`, `.env.lan` и реальные токены не коммитятся.

Критичные настройки:

```text
GPU_WORKER_MODE=container       # LAN release
WHISPERX_MODEL=large-v3
COMPUTE_TYPE=int8_float16
GPU_CONCURRENCY=1
ENABLE_ALIGNMENT=true
ENABLE_DIARIZATION=true
AUTO_SUMMARY_ENABLED=true
ASSISTANT_ENABLED=true
TRANSCRIPTION_START_DELAY_SECONDS=0
LLM_IDLE_UNLOAD_SECONDS=900
ASSISTANT_MAX_OUTPUT_TOKENS=384
```

Секреты `POSTGRES_PASSWORD`, `BOOTSTRAP_ADMIN_PASSWORD`, `SUPERVISOR_HEALTH_TOKEN`, `HF_TOKEN`,
`VOICE_HOST_TOKEN`, agent/import/TUS tokens не должны попадать в Git, логи,
manifest, diagnostics или acceptance JSON.

## 10. Запуск, обновление и recovery

### Сервер

1. Проверить `.env.lan`, свободное место и release manifest.
2. Выполнить backup/inventory до переключения образов.
3. `scripts/start-server-bundle.ps1` применяет additive migrations и запускает
   immutable release images.
4. `scripts/supervise-server-runtime.ps1` после входа пользователя ждёт Docker
   Desktop, проверяет containers и authenticated readiness.
5. Для диагностики использовать `scripts/doctor-whisperx-lan-server.ps1`.

Supervisor не выполняет `prune`, `down -v`, удаление volumes или действия с
Patrol360.

### Windows-клиент

1. Preflight проверяет отсутствие активной записи/финализации.
2. `scripts/publish-desktop.ps1` собирает Desktop, Recorder Host, Voice Host и
   TtsHost с одной identity.
3. Inno Setup устанавливает staged payload, сохраняя ProgramData, spool,
   archive, DPAPI и настройки.
4. Updater ждёт завершения записи, запускает UAC update и проверяет rollback.

### Recovery-инварианты

- один job/session/track/chunk/finalize не должен дублироваться;
- lease и inbox могут быть просрочены, но durable job остаётся источником истины;
- первый GPU timeout requeue-ит тот же job, второй становится terminal failure;
- V1/media/meeting/correlation не удаляются из-за ошибки V2/Summary;
- остановка отдельного worker не должна удалять volumes и пользовательские записи.

## 11. Тесты и приёмка

Локальные проверки:

```powershell
py -m pytest -q
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-dotnet-tests.ps1 -Configuration Release -NoRestore
dotnet build apps\server\WhisperX.Atom.Api\WhisperX.Atom.Api.csproj -c Release --no-restore
dotnet build apps\desktop\WhisperX.Atom.Desktop\WhisperX.Atom.Desktop.csproj -c Release --no-restore
```

Обязательная runtime-матрица:

```text
server off → START → STOP → local WAV READY → restart Desktop
→ server on → upload → V1 → V2 → Summary

без записи → GENERAL_CHAT → Qwen → Silero
во время записи → общий вопрос не становится автоматически LIVE_MEETING
команда «запусти запись» → Recorder, без Qwen
«Мифодий, пока» → локальный Farewell, сервер не нужен
```

Перед объявлением production readiness должны совпасть:

- clean build identity Desktop/Recorder/Voice/TtsHost/API/workers;
- authenticated readiness и свежие heartbeat;
- отсутствие orphaned GPU jobs;
- все migration checksums;
- отсутствие duplicate pipeline entities;
- успешный offline/reconnect/restart gate;
- один TTS playback и фактический Silero без fallback.

## 12. Правила изменения системы

Изменения, требующие отдельного review:

- Recorder IPC v6 и локальные state names;
- raw-first порядок PCM → playable/FLAC → network;
- RBAC, meeting scope и evidence grounding;
- GPU priority/lease и `GPU_CONCURRENCY=1`;
- миграции, volumes, retention и backup/restore;
- единая release identity;
- любые действия, которые могут затронуть Patrol360 или пользовательские записи.

Для деталей используйте:

- [Справочник модулей](module-reference.md);
- [Текущая архитектура](architecture-current.md);
- [API и IPC](api-and-ipc.md);
- [Recording module reference](recording-module-reference.md);
- [Transcription/Summary runtime](transcription-summary-runtime.md);
- [Мифодий: текущее состояние](mifodiy-current-state.md);
- [Конфигурация](configuration.md);
- [Операционный host runtime](operations-host-runtime.md).

## 13. Mifodiy Intelligence v2

После того как `AssistantModeResolver` определил scope (`GENERAL_CHAT`,
`CURRENT_MEETING`, `LIVE_MEETING` или `MEETING_MEMORY`), Assistant строит
детерминированный `AssistantQueryPlan`. Он содержит intent, тему, человека,
срок, follow-up и обязательные поля ответа. План не является evidence и не
может расширить meeting/RBAC scope.

`RetrievalPlan` выбирает лимиты и окно соседних сегментов по intent. Полученные
сегменты группируются в evidence bundles с исходными `segmentId`, временными
границами и `transcriptVersion`; candidate facts в bundle являются только
производным индексом. `AnswerPlan` передаёт Qwen явную политику: использовать
только подтверждённые источники, не выводить причинность из соседних фраз,
сообщать противоречия и возвращать `PARTIAL`, если обязательное поле не найдено.

Миграция `049_transcript_facts.sql` добавляет индекс явных decision/task/
responsible/deadline/cause/status фактов. Каждый факт ссылается на transcript,
его версию и сегменты; при появлении более новой версии старые rows получают
`INVALIDATED` и не используются как источник истины. Миграция
`050_assistant_conversation_state.sql` хранит только структурированный
follow-up state (тема, intent, человек, диапазон дат, последний вопрос и
meeting scope), никогда текст ответа Assistant и не заменяет transcript
evidence.

В ответном metadata доступны `queryPlan`, `retrievalPlan`, `answerPlan`,
`sourceRanges`, `supportedFields` и `missingFields`. Это позволяет Desktop
показать источник и честно сообщить о неполном ответе, не проговаривая
неподтверждённый факт.

Для локальной проверки intent используется синтетический reasoning corpus:
`scripts/run-mifodiy-reasoning-qa.py`. Он генерирует матрицу из 400 кейсов
(детальная таблица плана содержит именно 400, хотя в заголовке этапа указано
«300+»), проверяет уникальность категорий и сохраняет только SHA256 вопросов,
intent и метрики. Производственный runner
`scripts/run-mifodiy-qa.ps1` по-прежнему запускается отдельно и требует
явной авторизации; реальные вопросы, ответы и стенограммы в отчёт не попадают.

## 14. Meeting Memory v2 (foundation)

Meeting Memory v2 — это owner-scoped индекс подтверждённых фактов, а не
память ответов Qwen. Источником истины остаются `transcript_segments` и
canonical V1/V2. Индекс используется только для поиска кандидатов, после чего
фрагменты повторно поднимаются из canonical transcript и проходят обычный
grounding/RBAC-контур Assistant.

Добавленные additive-миграции:

- `051_memory_entities.sql` — нормализованные сущности и связи fact/entity;
- `052_memory_fact_relations.sql` — `SUPERSEDES`, `CONTRADICTS`, `CLOSES` и
  другие derived relations;
- `053_memory_threads.sql` — длительные owner-scoped темы и их факты;
- `054_memory_jobs.sql` — отдельная фоновая очередь индексации;
- `055_memory_invalidation.sql` — версия, породившая invalidation старого
  derived fact.

`workers/memory_worker` содержит детерминированные этапы extraction,
normalization, relation resolution, temporal selection, thread projection и
canonical evidence rehydration. Стадии Memory Worker: `QUEUED`,
`EXTRACTING_FACTS`, `RESOLVING_ENTITIES`, `LINKING_FACTS`,
`REBUILDING_THREADS`, `READY`, `NEEDS_REVIEW`, `FAILED`. Падение индексации не
переводит готовую V1/V2/Summary в ошибку.

Для `MEETING_MEMORY` Assistant сначала пробует memory index. При отсутствии
миграций или индекса автоматически используется существующий bounded FTS /
embedding retrieval. Memory-кандидат без `evidence_segment_ids`, stale fact,
чужой meeting или недоступный по RBAC сегмент не передаётся в Qwen.
