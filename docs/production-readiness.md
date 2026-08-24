# Production Readiness — WhisperX Atom / Мифодий

> Актуально для исходного кода на 24 августа 2026 года. Документ описывает
> рабочую ветку и release-gates, а не обещает готовность установленного runtime.

## Идентичность и границы

| Объект | Значение | Статус |
| --- | --- | --- |
| Ветка | `codex/server-first-platform` | текущая разработка |
| Implementation baseline | `894966a` (`llm: make doctor v2 probes fail closed`) | подтверждён локально; после docs-коммита identity нужно пересчитать |
| Docker runtime | `1.0.1-21a28029a49b` | старое поколение, не совпадает с source |
| Миграции | без новых миграций в текущем проходе | не изменялись |

Исходники не примонтированы в контейнеры. Поэтому изменение файлов в рабочем
дереве не обновляет Server Node: для обновления нужны новый immutable bundle,
образы и штатное переключение runtime. Docker volumes, пользовательские записи,
модели и Patrol360 в этом проходе не изменяются.

## Архитектурные инварианты

- `AudioGraph` и canonical Recorder остаются источником оригинального аудио.
- LLM и Voice Refiner не имеют права менять Recorder. `START`, `PAUSE`,
  `RESUME` и `STOP` проходят только deterministic allowlist и fail-closed gates.
- Вопросы идут через Desktop Broker → Assistant API → retrieval/grounding →
  Qwen. История Assistant не является evidence.
- Meeting-ответы используют только canonical transcript segments после повторной
  проверки владельца, meeting scope, transcript version, quality и evidence IDs.
- При отсутствии подтверждения ответ завершается `NO_EVIDENCE`, без подстановки
  имён, дат, чисел, причин или решений.
- Падение Refiner/LLM не ломает запись, V1, V2 или deterministic summary.
- Voice evidence, telemetry и support bundles не содержат PCM, расшифровку,
  вопросы или ответы пользователя.

## Voice Refiner и режимы

`VOICE_ASR_REFINER_MODE` имеет кумулятивные значения:

| Режим | Поведение |
| --- | --- |
| `OFF` | Refiner выключен; Vosk-контур работает самостоятельно |
| `SHADOW` | Refiner сравнивает wake/intent и публикует только метрики |
| `ASSISTANT_ONLY` | После локального ACK уточняет только Assistant-вопросы; при timeout используется исходный Vosk-текст |
| `WAKE_AUDIT` | `ASSISTANT_ONLY` плюс аудит wake; ничего не блокирует |

Неизвестное значение даёт `VOICE_REFINER_MODE_INVALID` и безопасно отключает
Refiner. `VOICE_REFINER_THREADS` принимает только `1`, `2` или `4` (диапазон
`1..4`); некорректное значение публикует `VOICE_REFINER_THREADS_INVALID` и
использует безопасный default `1`.

Локальные команды с вопросом, отрицанием, условием, будущим временем или
цитированием не меняют Recorder. Command-shaped, но не прошедшая safety gate
фраза получает `AMBIGUOUS_LOCAL_COMMAND` (legacy alias:
`VOICE_COMMAND_REPEAT_REQUIRED`) и не отправляется в Qwen.

### Assets и аттестация

Production assets обязательны и не скачиваются во время установки:

- whisper.cpp: `f049fff95a089aa9969deb009cdd4892b3e74916`;
- multilingual `ggml-small.bin`: revision
  `c521a4b02f422512d734391fdf08bb08c0862f68`;
- модель SHA256:
  `1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b`;
- bridge ABI: `1`.

Manifest schema v2 должен содержать `buildIdentity`, provenance, размер и SHA256
модели/bridge, `whisperCppRevision`, `bridgeRevision` и ABI. Installer fail-closed
при отсутствии или несовпадении этих данных. Единственный обход — development
сборка с явным `DevelopmentNoVoiceRefinerAssets` и
`VOICE_ASR_REFINER_MODE=OFF`.

## Deterministic Summary v2

`deterministic-v2` — безопасный fallback, а не замена LLM. Он использует
canonical segments, ACTIVE facts актуальной версии transcript и recording
markers. Derived fact перед использованием повторно rehydrate-ится по
`evidenceSegmentIds`; устаревшее значение fact не считается доказательством.

Поддерживаемые типы: `DECISION`, `TASK`, `RESPONSIBLE`, `DEADLINE`, `CAUSE`,
`STATUS`; `RISK` и `OPEN_QUESTION` принимаются только при однозначном evidence.
При недоступной LLM сохраняются `DETERMINISTIC_FALLBACK`,
`VERIFIED_PARTIAL`/`NEEDS_REVIEW` и `LLM_ENHANCEMENT_PENDING=true`. После
восстановления Qwen тот же summary job может быть идемпотентно улучшен.

## LLM Doctor v2 и Grounding v3.1

Doctor обязан проверить все probes и возвращает только состояния и timings:

`LLM_RUNTIME`, `ASSISTANT_JSON`, `ASSISTANT_GROUNDING`, `SUMMARY_JSON`,
`MEETING_PROTOCOL_RU` → `READY` или `FAILED`. Generated content в release logs
не выводится. Отсутствующая/повреждённая модель даёт `MODEL_INVALID`, а не
ложный `READY`; core WhisperX при этом может оставаться готовым.

Structured claim проверяет `subject`, `predicate`, `value`, `polarity` и
`evidenceIds`. Имена, числа и даты сравниваются точно; `CAUSE` требует явного
causal marker в canonical evidence. Недостающий обязательный атрибут переводит
ответ в `PARTIAL` либо отклоняет его.

## Проверки

### Автоматические проверки

Python test environment подключается к Python 3.12 из pinned offline wheelhouse:

```powershell
py -3.12 -m pip install --no-index `
  --find-links artifacts/python-test-wheelhouse-source `
  --require-hashes -r requirements.test.lock.txt
py -3.12 -m pytest -q
```

Дополнительные targeted проверки:

```powershell
dotnet test tests/WhisperX.Atom.Voice.Tests/WhisperX.Atom.Voice.Tests.csproj --no-restore -c Release
dotnet test tests/WhisperX.Atom.Api.Tests/WhisperX.Atom.Api.Tests.csproj --no-restore -c Release
py -3.12 scripts/run-mifodiy-reasoning-qa.py --production
py -3.12 scripts/run-mifodiy-intelligence-e2e.py
pwsh -NoProfile -File scripts/run-dotnet-tests.ps1 -TimeoutSeconds 60
pwsh -NoProfile -File scripts/voice-refiner-thread-benchmark.ps1 -Mode Installed `
  -BuildIdentity ((Get-Content artifacts/desktop/build-identity.json | ConvertFrom-Json).buildIdentity)
```

Последний локальный результат исходного дерева: Python `827 passed, 62 skipped`,
Voice `33/33`, API `20/20`; Voice Host Release build/self-test и PowerShell
parser checks прошли. Skips относятся к внешним PostgreSQL/GPU/железным
условиям и не превращаются в production `PASSED`.

### Voice corpus и far-field

Fixture replay остаётся только диагностическим. Production evidence создаётся
live-run на реальном микрофоне без сохранения аудио или текста:

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

Для corpus требуется минимум 700 вручную подтверждённых случаев. Матрица
far-field: `0.5/1/2/3 м` × `quiet/office/ventilation/conversation/TTS playback`,
не менее 20 повторов на ячейку. В acceptance JSON сохраняются только IDs,
категории, metrics и attestation.

### Release gate

```powershell
pwsh -NoProfile -File scripts/release-gate.ps1 `
  -OutputRoot artifacts/acceptance/release
```

Gate проверяет authenticated runtime, core chain, verified Voice assets,
benchmark, live corpus, far-field, Mifodiy QA и полную registry сценариев.
`BLOCKED`, `BLOCKED_BY_HARDWARE`, `CONTRACT_ONLY`, отсутствующая identity или
offline preflight не считаются `PASSED`.

## Текущий release status

| Gate | Статус | Причина |
| --- | --- | --- |
| Python/.NET source tests | `PASSED` | локальные suites зелёные |
| Voice parser/refiner contracts | `PASSED` | 33/33 и self-test |
| Voice verified assets | `BLOCKED` | финальные DLL/model/manifest v2 для identity не staged |
| Thread benchmark | `BLOCKED` | нет release-attested 1/2/4 evidence |
| 700-case Voice corpus | `BLOCKED_BY_HARDWARE` | нужен live microphone run |
| Far-field matrix | `BLOCKED_BY_HARDWARE` | fixture replay не может пройти release gate |
| Authenticated 700-case Mifodiy QA | `BLOCKED` | offline preflight не заменяет Assistant API |
| LLM Doctor on current runtime | `BLOCKED` | runtime/model probe не подтверждён |
| Docker/source identity | `BLOCKED` | Docker работает на старом image tag |

Пока любой из этих блоков не закрыт, production-ready и новый production
installer не объявляются. `artifacts/acceptance` не должен содержать аудио,
стенограмм, токенов или секретов. Старые bundle/image/volume удалять можно
только после backup, успешной приёмки новой identity и сохранения rollback.

## Что делать оператору дальше

1. Подготовить pinned Voice assets и получить новый clean `buildIdentity`.
2. Выполнить 1/2/4-thread benchmark и выбрать минимальный SLA-прошедший режим.
3. На Client Node провести live corpus и far-field matrix, сохранив только
   privacy-safe evidence.
4. Запустить authenticated Mifodiy QA (700+ случаев) против того же runtime.
5. Собрать Server Bundle и installer из одной identity, затем выполнить
   установленный smoke. До этого Docker и пользовательские данные не менять.
