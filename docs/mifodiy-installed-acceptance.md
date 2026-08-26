# Установленная приёмка «Мифодия»

`e2e-mifodiy.ps1` — отдельный gate для проверки уже собранного контура. Он не меняет Docker, не создаёт встречи и не сохраняет вопросы, ответы, аудио или текст стенограммы в acceptance JSON.

## Что проверяется

```text
Мифодий → Desktop Broker → requestedMode=AUTO → AssistantModeResolver
        → GENERAL/LIVE/CURRENT/MEMORY → Russian FTS → Qwen
        → claims/evidence → grounding → Microsoft Irina
```

Установленный режим выполняет:

- безопасный parser gate установленного Voice Host: `START → PAUSE → RESUME → STOP`
  и три русских вопроса (`кто отвечал за ремонт`, `какой срок назвали`, `что
  решили по насосу`); этот шаг не открывает Recorder и не вызывает Desktop
  Broker;
- 50 (или указанное число) wake/command тестов через установленный Voice Host;
- общий вопрос без встречи (`AUTO → GENERAL_CHAT`);
- вопрос с ожидаемым evidence (`AUTO → CURRENT_MEETING` или `LIVE_MEETING`
  только при наличии live evidence);
- три прикладных вопроса по текущему совещанию: ответственный за ремонт,
  названный срок и решение по насосу;
- follow-up «А кто отвечает?» с тем же серверным `conversationId` и scope;
- исторический вопрос без активной встречи (`AUTO`, без разрешения в
  `GENERAL_CHAT`);
- вопрос, для которого evidence не должно существовать;
- вопрос с датами/числами;
- проверку границы `CURRENT_MEETING`;
- опциональный production-path через установленный Voice Host control pipe:
  `TEXT → VoiceIntentParser → Desktop Broker → AUTO → AssistantModeResolver`.
  Для него
  используется `-RunBroker`; три вопроса должны вернуть `queryId`, быть приняты
  в playback и завершиться только по текущей встрече. Этот режим требует
  открытого Desktop с выбранной встречей и может произнести короткое
  подтверждение/ответ через Microsoft Irina;
- длинный one-shot вопрос;
- logout и проверку, что старый API-сеанс больше не действует;
- опциональный контролируемый перезапуск Desktop и проверку voice tombstone.

Ответы `NO_EVIDENCE`, `LOW_TRANSCRIPT_QUALITY` и `GROUNDING_REJECTED` считаются безопасными только как отрицательные ответы. Скрипт не публикует их как подтверждённый факт и проверяет допустимый короткий текст голосового отказа.

## Режимы

По умолчанию запускается безопасная проверка контракта без сети и микрофона:

```powershell
pwsh -NoProfile -File scripts/e2e-mifodiy.ps1 -Mode Contract
```

Это даёт `CONTRACT_ONLY`, а не готовность runtime.

Установленная приёмка требует явного пароля и команды захвата микрофона:

```powershell
$env:BOOTSTRAP_ADMIN_PASSWORD = 'пароль-в-секретном-хранилище'
pwsh -NoProfile -File scripts/e2e-mifodiy.ps1 `
  -Mode Installed `
  -RunMicrophone `
  -MeetingId '<meeting-id>' `
  -OutputPath artifacts/acceptance/mifodiy-v1.json
```

`-RunMicrophone` запускает только dry-run Voice Host (`--mic-acceptance`): запись, Recorder IPC и серверные мутации не выполняются. Для настоящего gate нужны установленный Voice Host под `Program Files`, доступный русский микрофон и готовая встреча с V1/V2.

Parser gate запускается автоматически в `Installed` режиме через
`--command-acceptance`. Он проверяет именно установленный бинарник и
возвращает `recorderInvocations=0` и `brokerInvocations=0`; фактический
микрофонный прогон остаётся отдельным dry-run и не может случайно начать или
остановить пользовательскую запись.

Чтобы проверить именно живой путь Voice Host → Desktop Broker, добавьте
`-RunBroker` к установленному запуску:

```powershell
pwsh -NoProfile -File scripts/e2e-mifodiy.ps1 `
  -Mode Installed -RunMicrophone -RunBroker `
  -MeetingId '<meeting-id>' `
  -OutputPath artifacts/acceptance/mifodiy-v1.json
```

Перед этим откройте Desktop и нужное совещание. `-RunBroker` не управляет
Recorder: он отправляет только три вопроса через Voice Host `TEXT`, ожидает
серверные `queryId/status/evidence` и фиксирует в JSON только идентификаторы,
статусы и количество evidence.

Для отдельного live-контекста во время реальной записи можно явно передать
идентификатор сессии и включить `-RunLive`:

```powershell
pwsh -NoProfile -File scripts/e2e-mifodiy.ps1 `
  -Mode Installed -RunLive -MeetingId '<meeting-id>' `
  -RecordingSessionId '<recording-session-id>'
```

Этот шаг считается успешным только при `AUTO → LIVE_MEETING`, совпадающем
`meetingId`, непустом evidence и terminal-статусе. Без `-RunLive` он не
блокирует обычную acceptance.

Контролируемый перезапуск Desktop не выполняется по умолчанию. Его можно включить отдельно после проверки отсутствия активной записи:

```powershell
pwsh -NoProfile -File scripts/e2e-mifodiy.ps1 `
  -Mode Installed -RunMicrophone -RestartDesktop -MeetingId '<meeting-id>'
```

Скрипт закрывает только Desktop с путём под `Program Files`, не завершает неизвестные процессы и не использует `Stop-Process`.

## Acceptance JSON

`artifacts/acceptance/mifodiy-v1.json` содержит только:

- статусы и коды ошибок;
- `queryId`, `conversationId`, `commandId`, `traceId`;
- идентификаторы выбранных встреч и evidence без текста;
- build identity и путь установленного Voice Host/Desktop/Recorder;
- количество принятых aliases, drops и false activations;
- результаты recorder-command parser gate без текста фраз;
- результаты broker-gate без текста вопросов и ответов;
- отметки logout и duplicate-delivery;
- явный блок `safety`.

В файл намеренно не попадают credentials, cookies, tokens, аудио, пути аудио, вопросы, ответы и текст стенограммы.

## Критерий результата

- `CONTRACT_ONLY` — контрактная проверка прошла, runtime не запускался;
- `PASSED` — единая установленная identity, voice gate, grounded CURRENT_MEETING, безопасные отрицательные ответы, logout и duplicate-delivery прошли;
- `BLOCKED` — хотя бы один обязательный runtime gate не выполнен. Причина находится в `errors` или `errorCode`, а не скрывается статусом «готово».

До `PASSED` нельзя объявлять `VOICE_ASSISTANT_READY`.
