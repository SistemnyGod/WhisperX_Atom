# «Мифодий»: фактическое состояние и план завершения

Дата последнего обновления: 25 августа 2026 года.

Этот документ отделяет реализованный код от установленного runtime и от
запланированных функций. Для release identity, Voice assets, hardware gates и
актуальных блокеров приоритет имеет [Production readiness](production-readiness.md).

## Важное обновление RC

Последний clean source — `4b5937d847fd830172fa7eaf9821fed738760720`.
На его основе собран клиентский pilot installer с единой identity
`1.0.1+4b5937d847fd830172fa7eaf9821fed738760720`. Пакет содержит Desktop,
Recorder Host, Voice Host, Resident Voice Refiner Host и TtsHost; verified
Voice model/bridge и manifest v2 прошли локальную проверку.

Pilot не включает Server Bundle и не переключает Docker runtime. Поэтому
серверная identity, authenticated Assistant/Qwen smoke, thread benchmark,
live 700-case corpus и far-field matrix по-прежнему требуют отдельного
приёма. Текущий статус — `UNSIGNED_PILOT_BUILD / BLOCKED_BY_HARDWARE`, а не
`PRODUCTION_READY`. Docker volumes, записи, модели и Patrol360 не изменялись.

## Обозначения готовности

| Статус | Значение |
| --- | --- |
| `IMPLEMENTED` | Функция присутствует в текущем рабочем дереве |
| `AUTOMATED_VERIFIED` | Сборка, self-test или контрактные тесты проходят |
| `RUNTIME_REQUIRED` | Код есть, но нужна установленная проверка с микрофоном и сервером |
| `NOT_IMPLEMENTED` | Для сценария отсутствует обязательная часть |

## Целевая граница компонентов

```text
выбранный микрофон
  → Voice Host / wake Vosk + unrestricted utterance Vosk
  → Desktop Voice Broker
      ├─ RecordingCommandService → Recorder Host
      └─ пользовательская API-сессия → Assistant API
          → Russian FTS → Qwen3-8B → grounding
          → Desktop → Voice Host → TtsHost/Silero `eugene`
```

Voice Host не получает пользовательский API token, не управляет Recorder
напрямую и не имеет доступа к spool. Все серверные вопросы идут через Desktop
Broker и текущую пользовательскую сессию.

## Что реализовано

### Lifecycle и приватность

- `IMPLEMENTED`: скрытый Voice Host запускается только вместе с Desktop.
- `IMPLEMENTED`: путь, PID, build identity и SID процесса проверяются перед
  использованием; глобальный mutex запрещает второй экземпляр.
- `IMPLEMENTED`: Desktop supervisor ограниченно перезапускает аварийно
  завершившийся Host и отправляет `SHUTDOWN` при закрытии приложения.
- `IMPLEMENTED`: используется выбранный Recorder endpoint; для фиксированного
  устройства запрещён молчаливый переход на другой микрофон.
- `IMPLEMENTED`: аудио до wake word существует только в памяти, проходит через
  bounded queue и не попадает в Recorder spool или сеть.
- `IMPLEMENTED`: live-телеметрия содержит RMS, peak, clipping, signal state,
  sequence и фактически открытый endpoint, но не содержит аудио.

### Wake word, разговор и команды записи

- `IMPLEMENTED`: основное имя — «Мифодий»; поддерживаются «Мефодий» и временный
  alias «Атом».
- `IMPLEMENTED`: wake-word «Мифодий» активирует существующий Vosk-контур и не
  зависит от профиля TTS. `MIFODIY_TECH` и `CLEAN` — это два варианта голоса
  ответа одного Мифодия, а не два ассистента и не два независимых контура.
- `IMPLEMENTED`: bundled Vosk small RU использует фонетический режим
  `Мефодий`; exact-режим разрешён только для модели с соответствующим токеном.
- `IMPLEMENTED`: START, STOP, PAUSE, RESUME, STATUS, marker, decision и action
  item распознаются до Assistant routing.
- `IMPLEMENTED`: после wake word любой уверенно распознанный некомандный текст
  получает канонический `VoiceIntent.AssistantQuery` и проходит в
  `ASSISTANT_QUESTION`; старое имя `HistoryQuestion` оставлено только как
  enum-алиас. Пустые, низкоуверенные, с NaN/Infinity в confidence и неразборчивые результаты
  остаются `Unknown`. Voice Host не пытается определить тип бизнес-контекста
  и не содержит LLM-логики.
- `IMPLEMENTED`: Desktop Broker передаёт `requestedMode=AUTO` и активный
  `meetingId` в API. Серверный `AssistantModeResolver` сначала пробует
  retrieval в scope follow-up, затем live-контекст активной записи, текущую
  встречу и history-память; только отсутствие сильного Russian FTS/evidence
  совпадения в явно общем вопросе приводит к `GENERAL_CHAT`. Явно
  исторический/совещательный вопрос без совпадения остаётся в
  `MEETING_MEMORY`, чтобы worker вернул `NO_EVIDENCE`, а не придумал ответ из
  общих знаний. Capture state, transcript quality и RBAC применяются на
  стороне API/БД, поэтому текстовый и голосовой клиенты используют единый
  routing contract, а ключевые слова остаются лишь подсказкой.
- `IMPLEMENTED`: низкоуверенный STOP требует отдельного подтверждения в течение
  десяти секунд; повтор одной команды в течение двух секунд подавляется.
- `IMPLEMENTED`: кнопки Desktop и голос используют один
  `RecordingCommandService`. TTS START выполняется после Recorder ACK, а STOP
  сообщает «сохранена» только для `LOCAL_READY`.
- `IMPLEMENTED`: `commandId`, `traceId`, `responseId` и `localSessionId`
  связывают команду, Recorder и системный ответ.

### TTS и технические интервалы

- `IMPLEMENTED`: основной путь — локальный Silero `v5_5_ru`, speaker `eugene`,
  профиль `MIFODIY_TECH`; `CLEAN` оставляет чистый `eugene`.
- `IMPLEMENTED`: `MIFODIY_TECH` применяет только на playback high-pass около
  70 Гц, presence около 2,8 кГц, мягкую компрессию, лёгкую saturation и
  limiter `-1 dBFS`. TTS WAV, Recorder archive, Voice ASR и WhisperX input не
  изменяются.
- `IMPLEMENTED`: при ошибке FX используется чистый `eugene`, при отказе
  Silero — русский Windows fallback. Все TTS-профили остаются русскими и
  не используют имитацию голоса актёра.
- `IMPLEMENTED`: snapshot дополнен additive-полями `voiceProfile`,
  `fxEnabled`, `fxApplied`, `fxFallbackReason`; cancellation, ducking и ровно
  одно terminal-воспроизведение сохраняются.
- `IMPLEMENTED`: legacy WAV-ответы не читаются runtime и исключены из новой
  публикации Voice Host.
- `IMPLEMENTED`: `SYSTEM_RESPONSE_STARTED/FINISHED` создают sample-based
  технические интервалы. Сервер скрывает пересекающиеся технические сегменты
  из пользовательской стенограммы.
- `IMPLEMENTED`: принятие Assistant-вопроса не озвучивает шаблонное
  «вопрос принят» и не дублирует ответ. Voice Host возвращается к listening,
  а в TTS попадает только поздний grounded `voice_answer`; во время реального
  playback обе provisional-дорожки подавляются и возобновляются после
  короткого tail-интервала.

### Текстовый и голосовой Assistant

- `IMPLEMENTED`: Desktop имеет страницу «ИИ-помощник», постоянные диалоги,
  сообщения, источники и переход к сегменту по таймкоду.
- `IMPLEMENTED`: после durable-приёма сообщения Desktop ждёт ответ в
  foreground не более 15 секунд. Если Qwen холодный или SSE временно завис,
  composer освобождается, а открытый чат обновляется раз в три секунды до
  terminal-статуса; запрос не создаётся повторно.
- `IMPLEMENTED`: публичный пользовательский режим — `AUTO`; серверный resolver
  выбирает `GENERAL_CHAT`, `CURRENT_MEETING`, `MEETING_MEMORY` или
  `LIVE_MEETING`. `MEETING_HISTORY` сохраняется как обратно совместимый alias
  `MEETING_MEMORY`.
- `IMPLEMENTED`: `CURRENT_MEETING` получает активный `meetingId` из открытой
  карточки совещания. Ordinary user ищет только по собственным доступным
  встречам; расширенный scope разрешён серверной ролью.
- `IMPLEMENTED`: retrieval использует PostgreSQL Russian FTS, до 12 основных
  результатов, соседние сегменты, максимум 36 сегментов, пять встреч и 36 000
  символов.
- `IMPLEMENTED`: поверх scope-filtered FTS добавлен hybrid rerank с локальными
  embeddings, русской нормализацией/синонимами и соседями той же transcript.
  Evidence snapshot по-прежнему фиксируется до Qwen; provider и размеры
  candidate set сохраняются только в техническом `answer_metadata`.
- `IMPLEMENTED`: скрытые технические сегменты и стенограммы с критическими
  quality warnings не передаются Qwen.
- `IMPLEMENTED`: Qwen3-8B возвращает экранный `answer`, короткий
  `voice_answer`, evidence IDs и claims. Voice answer ограничивается тремя
  предложениями и 500 символами.
- `IMPLEMENTED`: ошибка запуска/таймаута Qwen не превращается в пустой
  `READY`: worker сохраняет terminal failure, а исходный запрос остаётся
  видимым для retry.
- `IMPLEMENTED`: для meeting-режимов проверяются evidence IDs, пересечение
  содержательных токенов и числовые значения. При первой ошибке разрешена одна
  повторная генерация; затем возвращается `GROUNDING_REJECTED`.
- `IMPLEMENTED`: `NO_EVIDENCE` и `LOW_TRANSCRIPT_QUALITY` завершаются без
  публикации неподтверждённого ответа как готового факта.
- `IMPLEMENTED`: Assistant имеет GPU priority между ASR и Summary. Resident
  Qwen выключен; модель освобождается после задания.

### Автоматическое саммари

- `IMPLEMENTED`: `AUTO_SUMMARY_ENABLED` и `ASSISTANT_ENABLED` независимы.
- `IMPLEMENTED`: один Summary Worker обрабатывает `llm.assistant` и Summary;
  Qwen запускается через общий GPU lease.
- `IMPLEMENTED`: автоматическое Summary создаётся только после пригодной V2 и
  блокируется для `NO_SPEECH_DETECTED`, `ASR_LANGUAGE_MISMATCH`,
  `AUDIO_SIGNAL_UNUSABLE` и стенограмм, требующих проверки.

### LIVE-вопросы во время записи

- `IMPLEMENTED`: вопросы во время `RECORDING`, `PAUSED`, `STARTING` и
  `FINALIZING` не читают старую V1/V2. API атомарно переводит `AUTO` в
  отдельный `LIVE_MEETING` scope после проверки свежей provisional-памяти.
- `IMPLEMENTED`: Voice Host ведёт отдельную unrestricted Vosk-сессию только
  пока Recorder активен и публикует текстовые provisional-сегменты через
  Desktop Broker. Канонический PCM этого контура не меняется.
- `IMPLEMENTED`: live retrieval использует только свежие строки
  `live_meeting_segments`, с лексическим якорем и соседними фрагментами;
  отсутствие якоря даёт `LIVE_MEETING_NOT_READY`, без подстановки последних
  произнесённых слов.
- `IMPLEMENTED`: подтверждённый ответ live-помечается
  `ANSWERED_WITH_WARNING`, `provisional=true`, `canonicalTranscript=false` и
  не становится Transcript V1/V2 или основанием для Summary.
- `IMPLEMENTED`: live-память сохраняется на всё совещание и на переход
  `STOP → FINALIZING → V1`. Для новых сегментов действует safety-expiry семь
  дней, но обычная очистка выполняется сразу после пригодного V1. Хранилище
  ограничено 8192 сегментами на сессию; строки, вошедшие в
  `assistant_live_query_evidence`, не удаляются до завершения аудита.
- `IMPLEMENTED`: после появления V1 resolver больше не выбирает
  `LIVE_MEETING`: follow-up продолжает ту же беседу, но retrieval переключается
  на канонический V1 (а затем V2). Provisional-текст никогда не становится
  источником Summary или заменой V1/V2.
- `RUNTIME_REQUIRED`: качество live ASR и задержка ответа ещё требуют
  установленной проверки на реальном микрофоне в активной встрече.

## Что подтверждено автоматикой

- `AUTOMATED_VERIFIED`: Voice Core и Voice Host self-test проходят.
- `AUTOMATED_VERIFIED`: установленный Voice Host поддерживает безопасный
  `--command-acceptance` gate для `START/PAUSE/RESUME/STOP` и прикладных
  вопросов; режим имеет нулевые вызовы Recorder и Desktop Broker.
- `AUTOMATED_VERIFIED`: установленный acceptance script имеет явный `-RunBroker`
  режим для живого `TEXT → VoiceIntentParser → Desktop Broker → CURRENT_MEETING`
  пути; он выполняется только при открытом Desktop/совещании и сохраняет в
  acceptance JSON лишь query/status/evidence IDs.
- `AUTOMATED_VERIFIED`: Release-сборки Voice Host, Desktop и API проходят без
  ошибок и предупреждений.
- `AUTOMATED_VERIFIED`: целевые voice/assistant contract tests проходят.
- `AUTOMATED_VERIFIED`: Docker-контейнеры API, GPU Worker, Summary Worker,
  Media Worker, Import Worker, PostgreSQL и NATS запущены и имеют healthy
  status; API отвечает внутри Docker-сети.
- `AUTOMATED_VERIFIED`: `.env.lan` включает `ASSISTANT_ENABLED=true` и
  `AUTO_SUMMARY_ENABLED=true`.

Эти проверки относятся к ранее снятому healthy Docker inventory и не означают
совпадение image identity с текущим source. Они также не заменяют реальный
voice-to-answer gate.

## Текущее состояние установленного приложения

Записи ниже о старом установленном Desktop/Voice Host сохранены как история и
не являются текущей identity. Для текущей проверки действует следующая матрица:

| Контур | Текущее состояние |
| --- | --- |
| Исходники | clean `4b5937d847fd830172fa7eaf9821fed738760720` |
| Desktop/Voice Host | pilot identity `1.0.1+4b5937d847fd830172fa7eaf9821fed738760720` |
| Docker Server Node | отдельный runtime; этим client pilot не обновлялся |
| Voice-to-answer | `RUNTIME_REQUIRED`; нужен authenticated smoke с микрофоном |
| `VOICE_ASSISTANT_READY` | не выставлять до server/client identity, authenticated smoke и hardware evidence |

Исходники не монтируются в контейнеры. Поэтому healthy Docker не доказывает,
что в нём уже работают изменения текущей ветки.

## Найденные дефекты и незавершённые части

### P0 — блокирует голосовые вопросы

1. **Свободный вопрос распознаётся отдельной unrestricted Vosk-сессией.**

   Parser уже распознаёт «кто», «что», «покажи», «расскажи» и другие формы,
   Wake recognizer остаётся фиксированным и подтверждает только кодовое слово.
   После него full-vocabulary recognizer хранит короткую фразу только в
   памяти; затем parser детерминированно выбирает Recorder-команду или вопрос.

   Поддержаны one-shot и двухфазный режимы. Recorder-команды после
   распознавания по-прежнему проходят строгий deterministic allowlist.

2. **Клиентский pilot собран, но установленный smoke не завершён.**

   Desktop, Recorder Host, Voice Host, Refiner Host и TtsHost собраны из
   `4b5937d...` одной identity. Требуется подтвердить установку на целевом ПК,
   повторный вход и согласование с Server Bundle перед production rollout.

### P1 — надёжность и защита ответа

1. **Голосовые follow-up вопросы имеют scoped-память.** Desktop хранит только
   conversation ID с TTL 30 минут по ключу user/mode/meeting и очищает его при
   logout.

2. **Claims обязательны для meeting-режимов.** Ответ без непустых claims с
   допустимыми evidence IDs, именами, числами и фактами из источников не может
   стать READY.

3. **Prompt evidence фиксируется до Qwen.** Миграция `031` сохраняет
   `RETRIEVED` evidence до запроса модели и `CITED` evidence после ответа.

4. **Финальный голосовой ответ ожидает Desktop.** Voice Host больше не делает
   180-секундный polling. Desktop дедуплицирует query ID и доставляет terminal
   result через `SPEAK_ASSISTANT_RESULT`, пока он актуален.

5. **Logout очищает ActiveMeeting, conversation scope и pending queries** до
   удаления пользовательской API-сессии.

6. **TTS shutdown отменяем.** Windows `SpeakAsync` прерывается через
   `SpeakAsyncCancelAll`, а системное событие завершения получает
   `cancelled=true`.

### P2 — качество и сопровождение

1. Hybrid retrieval реализован как bounded offline-safe baseline. Для ещё более
   сильной семантики можно отдельно подготовить локальную
   sentence-transformers модель и включить её через
   `ASSISTANT_EMBEDDING_PROVIDER=sentence-transformers`; RBAC и meeting
   boundaries при этом не меняются.
2. Legacy `VoiceAssistantClient` с server-token контрактом удалён: Voice Host
   получает Assistant-результаты только через Desktop Broker и пользовательскую
   API-сессию.
3. Большая часть текущих contract tests проверяет наличие строк в исходниках.
   Нужны поведенческие тесты unrestricted question recognizer, voice memory,
   delayed Assistant result, logout isolation и строгого claims grounding.
4. Профиль голоса в Desktop выбирается из bounded списка `MIFODIY_TECH` и
   `CLEAN`; русский Windows fallback остаётся расширенной диагностической
   настройкой. Свободного выбора английского голоса нет.

## Порядок завершения

1. Реализовать wake-only → unrestricted question capture и тесты one-shot и
   двухфазного вопроса.
2. Ввести scoped voice conversation memory и очистку при logout/смене встречи.
3. Сделать claims обязательными и сохранять retrieval evidence до Qwen.
4. Передать ожидание результата Desktop и сделать durable TTS completion.
5. Исправить отмену TTS и подтвердить A/B для `MIFODIY_TECH`, `CLEAN` и
   русского Windows fallback.
6. Собрать чистый installer и установить его поверх текущей версии после
   согласования Server Bundle identity.
7. Выполнить установленный gate:

```text
Мифодий → вопрос по открытой встрече
→ пользовательская API-сессия
→ CURRENT_MEETING retrieval
→ Qwen → strict grounding
→ полный ответ и таймкоды в Desktop
→ короткий ответ TtsHost/Silero `eugene`
```

## Критерий `VOICE_ASSISTANT_READY`

- не менее 45 из 50 контрольных вопросов распознаны и маршрутизированы;
- `CURRENT_MEETING` ни разу не использует другую встречу;
- `NO_EVIDENCE` не запускает неподтверждённый голосовой ответ;
- follow-up вопрос использует только текущий scoped conversation;
- logout исключает перенос meeting context к другому пользователю;
- Assistant result не теряется при ожидании более трёх минут;
- spoken answer соответствует сохранённому `voice_answer` и имеет evidence;
- установленный Desktop и Voice Host имеют одну clean build identity.
- **Wake-word policy (RC):** production accepts `Мифодий` and the phonetic
  `Мефодий`. The historical `Атом/atom` alias is disabled by default via
  `WHISPERX_WAKE_COMPAT_ATOM=false`; enable it only for a controlled legacy
  rollout. The command-acceptance gate follows the same setting.
- **Latency gate:** `scripts/e2e-mifodiy-latency.ps1` polls the durable query
  at a bounded interval (default 100 ms), records stage transitions and
  time-to-first-audio, and writes only a question hash plus IDs/timings to
  its evidence file.
