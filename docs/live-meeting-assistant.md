# LIVE_MEETING: вопросы во время записи

`LIVE_MEETING` — отдельный контекст голосового помощника для активного
совещания. Он не снимает защиту Recorder простым условием и не использует
неполную каноническую стенограмму.

```text
Recorder Host
   ├─ room-microphone ──► LiveAudio v1 ──► Vosk LOCAL_ROOM
   └─ system-audio ─────► LiveAudio v1 ──► Vosk REMOTE_SYSTEM
                                      │
                              fusion + echo dedup
                                      │
                                      ▼
                         POST /api/assistant/live-segments/{meetingId}
        -> live_meeting_segments (TTL 5 минут, только текст и таймкоды)
        -> Voice Host -> Desktop Broker
        -> POST /api/assistant/requests {requestedMode: LIVE_MEETING}
        -> Summary Worker live_context
        -> Qwen + claims/evidence
```

## Границы данных

- В live-памяти нет PCM, FLAC, токенов или текста финальной V1/V2.
- Сервер принимает сегменты только при активной recording-сессии и проверяет
  владельца сессии (оператор может работать в разрешённом scope).
- Сегмент живёт не более пяти минут и ограничен 256 последними сегментами на
  сессию. Идентификатор и `revision` обеспечивают идемпотентное обновление.
- Фоновая recovery-задача удаляет истёкшие строки даже если новых аудиопакетов
  больше не приходит. Для запроса, который ещё выполняется, данные удерживаются
  не более 15 минут; после этого live-evidence также удаляется каскадно.
- Live evidence фиксируется в отдельной `assistant_live_query_evidence`, так
  как оно не является `transcript_segments` и не должно попадать в канонический
  реестр стенограмм.
- Успешный ответ по live-фрагментам получает статус
  `ANSWERED_WITH_WARNING` и метаданные `provisional=true,
  canonicalTranscript=false`. Он может быть озвучен, но не считается V1/V2 и
  не заменяет финальный ответ после обработки записи.

## Поведение Broker

Во время `Recording`, `Paused`, `Starting` или `Finalizing` вопрос принудительно
переводится в `LIVE_MEETING`. Если текущая встреча не открыта или ещё нет свежих
provisional-сегментов, возвращается `LIVE_MEETING_NOT_READY`; запрос не
перенаправляется в `CURRENT_MEETING`, историю или общий чат.

После STOP обычный `CURRENT_MEETING` снова использует только готовую V1/V2.
Ошибки live-контекста не блокируют запись и не меняют исходный аудиофайл.

## Производитель provisional ASR

Recorder Host владеет аудиодорожками и после continuity validation передаёт их в
отдельный локальный named pipe `WhisperXAtomLiveAudioV1`. Это не второй consumer
AudioGraph-канала: live tap копирует PCM уже после проверки, а durable PCM,
SQLite, FLAC и delivery никогда не ждут Vosk. На каждой дорожке есть bounded
очередь; при переполнении удаляется только provisional-кадр и увеличивается
счётчик drops. Каждое сообщение содержит `sessionId` и явный алиас
`localSessionId`, `meetingId`, `trackId`, роль дорожки, sequence и общую
session-relative временную шкалу.

Managed Voice Host держит два независимых unrestricted Vosk-сеанса:
`LOCAL_ROOM` для микрофона и `REMOTE_SYSTEM` для системного звука. System audio
никогда не подаётся в wake/command/cancel recognizer. Фраза получает источник,
роль дорожки, относительные таймкоды и quality flags. Одинаковые реплики в окне
±1,5 секунды сравниваются после нормализации (`ё/е`, регистр, пунктуация); при
сходстве от 0,85 остаётся системная копия, а разные одновременные реплики
сохраняются обе.

Если LiveAudio IPC недоступен, Voice Host временно использует микрофонный
`MIC_FALLBACK`; system-audio fallback не имеет. Аудио live остаётся только в
памяти, а в Broker/API уходят исключительно текстовые provisional-сегменты.
Публикация выполняется bounded single-flight операцией и никогда не блокирует
capture, SQLite, FLAC или delivery.

При выключенном loopback Voice Host работает в `MIC_ONLY`; в Desktop/диагностике
отдельно видны состояние микрофонной и системной дорожек, drops, опубликованные
и подавленные сегменты.

Командные фразы и участки wake/command-session отбрасываются из live-памяти.
Если Voice Host, Desktop Broker или API недоступны, сегмент теряется только как
provisional подсказка; Recorder продолжает сохранять канонический PCM. До
появления первого свежего сегмента Мифодий возвращает
`LIVE_MEETING_NOT_READY`, а не использует старую V1/V2 и не выдумывает ответ.

Если вопрос не содержит ни одного смыслового якоря в свежем окне, live
retrieval также возвращает `LIVE_MEETING_NOT_READY`, а не подставляет последние
произнесённые фразы как доказательство.

Фактический установленный runtime-gate с микрофоном остаётся отдельной
приёмкой: текущие проверки подтверждают контракт, сборку и fail-closed
поведение, но не заменяют проверку качества речи в конкретном помещении.
