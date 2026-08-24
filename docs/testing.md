# Тестирование и приёмка

## Быстрые проверки

```powershell
.\scripts\prepare-python-test-environment.ps1
.\scripts\run-python-tests.ps1 -PytestArguments @('-q','tests/test_transcript_runtime_contracts.py','tests/test_transcript_mvp_contracts.py','tests/test_nonweb_risks.py')
```

Для полностью автономного .NET restore на уже подготовленной Windows-машине
однократно скопируйте проверенный пользовательский cache в изолированный cache
репозитория:

```powershell
.\scripts\prepare-nuget-cache.ps1 -AllCached
.\scripts\run-dotnet-tests.ps1 -TimeoutSeconds 60
```

```powershell
dotnet build apps/server/WhisperX.Atom.Api/WhisperX.Atom.Api.csproj --no-restore
dotnet build apps/desktop/WhisperX.Atom.Desktop/WhisperX.Atom.Desktop.csproj --no-restore
.\scripts\check-desktop-ui-encoding.ps1
.\scripts\doctor-whisperx.ps1 -SkipRegistry
```

Для Recorder/Voice Host используйте соответствующие `.csproj`; Desktop acceptance умеет пропускать Docker и GPU через `-SkipDocker` и `-SkipGpu`.

## Реальный Transcript E2E

Перед запуском задайте корень приватного regression corpus. Само аудио не
хранится в Git, а manifest содержит только относительный путь и SHA256:

```powershell
$env:REGRESSION_AUDIO_ROOT = "D:\\WhisperX\\regression-audio"
$audio = Join-Path $env:REGRESSION_AUDIO_ROOT "SUMMIT_ бизнес-центр инструктажи.m4a"
if (-not (Test-Path -LiteralPath $audio -PathType Leaf)) { throw "REGRESSION_AUDIO_MISSING" }
```

```powershell
.\scripts\e2e-transcript.ps1 `
  -AudioPath $audio `
  -Runs 1
```

Путь к аудио разрешается только из `REGRESSION_AUDIO_ROOT`; это локальная
подсказка и не означает, что файл хранится в Git или отправляется во внешний
сервис. E2E создаёт отдельный run directory в `artifacts/transcription-mvp`,
запускает doctor и сохраняет result/logs/doctor output.

Проверять нужно не только exit code:

- новая цепочка содержит новый `meetingId`, `mediaAssetId`, `jobId` и transcript;
- segments не пустые;
- текст не пустой;
- quality report и quality score сохранены;
- статус — `READY` или обоснованный `PARTIAL_READY`;
- пустой и оборванный transcript не получает `READY`;
- повторный запуск не переиспользует старую встречу по имени файла;
- локальный архив и Recorder spool не изменяются деструктивно.

После первого green прогона:

```powershell
.\scripts\e2e-transcript.ps1 `
  -AudioPath $audio `
  -Runs 5
```

Для вертикальной проверки `V1 → V2 → Summary` добавьте `-WithSummary`.
Перед этим полный локальный runtime должен быть запущен через
`.\scripts\run-whisperx.ps1` без `-TranscriptOnly`; параметр поднимет профиль
`llm` и потребует готовый Summary Worker/Qwen.

## Ручные recovery-сценарии

1. Остановить host GPU Worker во время job и убедиться, что watchdog восстанавливает процесс, а job получает redelivery.
2. Отключить API во время записи: Agent должен сохранить archive/spool и продолжить после восстановления.
3. Прервать TUS upload: повторить клиент и проверить продолжение по `Upload-Offset`.
4. Перезапустить NATS/media/GPU worker: PostgreSQL job и локальная очередь должны сохраниться.
5. Выполнить `stop-whisperx.ps1` и затем `run-whisperx.ps1`: незавершённая очередь не удаляется.

## Progressive upload во время записи

Для длинной записи используйте отдельный gate, который не ждёт `STOP` перед
первой проверкой доставки:

```powershell
.\scripts\acceptance-progressive-upload.ps1 -Seconds 600 -ProbeAfterSeconds 300
```

В середине записи скрипт требует уже созданный `serverSessionId` и хотя бы один
подтверждённый chunk (`confirmedChunkCount > 0`). После `STOP` проверяется, что
счётчик подтверждённых chunks не уменьшился и хвост дошёл до `CONFIRMED` или
`COMPLETED`. Отчёт не содержит аудио, токены или credentials.

## Известные границы

- Полный diarization acceptance требует реальной двухспикерной записи.
- GPU E2E зависит от локального CUDA/WhisperX окружения и HF доступа.
- Qwen Summary/Assistant не является условием готовности Transcript MVP, если `AUTO_SUMMARY_ENABLED=false`.
- Исторические contract tests могут проверять структуру кода; для критических изменений приоритет имеют behavioral tests и реальный E2E.

## Python 3.12 и Mifodiy QA

Полный Python suite запускается только в подготовленной среде Python 3.12.
`pytest` не является системной зависимостью проекта: установите pinned lock из
локального wheelhouse без сети:

```powershell
py -3.12 -m pip install --no-index `
  --find-links artifacts/python-test-wheelhouse-source `
  --require-hashes -r requirements.test.lock.txt
py -3.12 -m pytest -q
```

Offline reasoning и synthetic A→B→C — это preflight, а не release acceptance:

```powershell
py -3.12 scripts/run-mifodiy-reasoning-qa.py --production `
  --output artifacts/acceptance/mifodiy-reasoning-preflight.json
py -3.12 scripts/run-mifodiy-intelligence-e2e.py `
  --output artifacts/acceptance/mifodiy-intelligence-e2e.json
```

Для production gate нужен `scripts/run-mifodiy-qa.ps1` с
`executionTarget=ASSISTANT_API`, реально выполненными случаями и ненулевым
числом executed cases. Local status-команды допускаются только в
`VOICE_LOCAL` behavioral runner; через Assistant API они являются ошибкой
корпуса. В отчёте должны быть IDs, hashes, metrics и pass-flags, но не вопрос,
ответ или evidence text.

## Voice acceptance: diagnostic против production

`scripts/e2e-far-field-voice.ps1` и fixture replay полезны для contract/diagnostic
проверки, но не могут сформировать production `PASSED`. Для release нужен live
операторский прогон:

```powershell
$identity = (Get-Content artifacts/desktop/build-identity.json | ConvertFrom-Json).buildIdentity
$voiceHost = "C:\path\to\WhisperX.Atom.Voice.Host.exe"
pwsh -NoProfile -File scripts/run-far-field-voice-live.ps1 `
  -VoiceHostPath $voiceHost `
  -VoiceManifestPath "apps/voice-host/Models/Voice/whisper-shadow/voice-refiner.manifest.json" `
  -BuildIdentity $identity -OperatorConfirmed `
  -OutputPath artifacts/acceptance/far-field-voice/live.json
pwsh -NoProfile -File scripts/voice-shadow-corpus.ps1 `
  -BuildIdentity $identity -CorpusRoot "C:\ProgramData\WhisperXAtom\QA\Mifodiy\voice-shadow" `
  -CaptureMode LIVE `
  -OutputPath artifacts/acceptance/voice-shadow-corpus/evidence.json
```

Требования: не менее 700 подтверждённых случаев, матрица
`0.5/1/2/3 м × quiet/office/ventilation/conversation/TTS playback`, минимум 20
повторов на ячейку, ноль ложных Recorder mutations/STOP, timeout rate менее 1%,
очередь без drops и совпадающая identity assets. Acceptance artifact не содержит
аудио, PCM, расшифровку или текст Assistant.

Актуальные результаты и причины `BLOCKED` перечислены в
[Production readiness](production-readiness.md). Не переводите `SKIPPED`,
`CONTRACT_ONLY` или `BLOCKED_BY_HARDWARE` в зелёный статус вручную.
### Far-field voice acceptance

The installed replay gate uses explicit WAV fixtures for every distance and
room condition. It never opens Recorder or sends a command to the Desktop
Broker:

```powershell
pwsh -NoProfile -File scripts/e2e-far-field-voice.ps1 -Mode Contract
pwsh -NoProfile -File scripts/e2e-far-field-voice.ps1 -Mode Installed `
  -FixtureRoot "C:\path\far-field-fixtures"
```

Fixtures are named `<distance>\<condition>.wav` for `0.5m`, `1m`, `2m`, `3m`
and `quiet`, `office`, `ventilation`, `conversation`. The gate is fail-closed
on missing fixtures, dirty builds, replay timeouts, low recall or false
activations. The result contains metrics only; audio paths and recognized
text are not written to the acceptance JSON.
