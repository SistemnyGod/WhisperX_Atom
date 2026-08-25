# Release gates

The ordinary CI checks are required before merge: `dotnet-build`, `python-targeted-tests`, `migration-validation`, and `desktop-resource-check`.

`optional-package-installer` runs only from workflow dispatch and is not a required pull-request check.

The following evidence is deliberately outside commit CI and must be recorded as READY before merging an MVP release branch or creating a tag:

- 5-minute end-to-end recording;
- server-offline recovery;
- Recorder crash recovery;
- worker crash recovery;
- Windows reboot and no-console startup (Docker Desktop/Supervisor must recover
  before a client is opened);
- no-console `20 × 10s` sequence with unique jobs and terminal V1/V2/Summary
  evidence;
- capture quality evidence (native and normalized clipping, RMS/noise/DC and
  frame continuity);
- live `audio-capture-parity` evidence for Audacity, AudioGraph, WASAPI Shared
  Native and RAW, with the native candidate still diagnostic-only;
- `transcription-quality-ab` evidence with a fixed reference transcript and
  privacy-safe WER/CER/domain-term aggregates;
- microphone and system-audio device-loss recovery;
- low-disk recording protection;
- cold model runtime and GPU-OOM recovery;
- delete-locked-media recovery;
- 30-minute, 2-hour, 4-hour and 8-hour endurance;
- backup/restore acceptance;
- RBAC isolation;
- verified Voice Refiner assets and manifest v2;
- `voice-refiner-thread-benchmark` for the selected 1/2/4-thread mode;
- live `voice-shadow-corpus` with at least 700 manually confirmed cases;
- live `far-field-voice` matrix for 0.5/1/2/3 m and quiet/office/ventilation/
  conversation/TTS playback;
- authenticated `mifodiy-intelligence-acceptance` with at least 700 executed
  Assistant API cases and complete expected/actual result flags.

Set `MVP_V1_READY=true` only after every required record is available. The
release script is fail-closed: a missing physical/hardware artifact, offline
preflight, fixture replay, mismatched identity, `BLOCKED` or
`BLOCKED_BY_HARDWARE` blocks the release even when all .NET/Python CI checks are
green. Create a release tag only after the protected `main` merge is green.

Acceptance artifacts live below `artifacts/acceptance/<scenario>/` and must be
JSON with `status` (or `result`) equal to `READY`, `PASSED` or `GREEN` (or
`passed: true`). They must contain metrics and IDs only; do not place audio,
transcript text, credentials or tokens in the reports. The required scenario
names are maintained in `scripts/acceptance-scenarios.json` and consumed by
both the release gate and hardware runner:

Для capture-quality отчёт можно получить установленным Recorder Host так:

```powershell
.\scripts\acceptance-audiograph-local-recording.ps1 `
  -Seconds 60 -ServerDelivery -StopHost `
  -OutputRoot artifacts\acceptance\audio-quality
```

Для `no-console-start`, device-loss, GPU-OOM и 4/8-часовых сценариев нужны
отдельные аппаратные прогоны на Server Node; CI и dry-run не заменяют эти
доказательства.

```text
e2e-5m
server-offline-recovery
recorder-crash-recovery
worker-crash-recovery
windows-reboot-recovery
no-console-start
no-console-20x10s
audio-quality
audio-quality-ab
audio-capture-parity
transcription-quality-ab
audio-device-loss
system-audio-device-loss
low-disk-during-recording
cold-model-runtime
gpu-oom
delete-locked-media
endurance-30m
endurance-2h
4h-recording
8h-recording
backup-restore
rbac-isolation
voice-refiner-thread-benchmark
voice-shadow-corpus
far-field-voice
mifodiy-intelligence-acceptance
```

If branch protection cannot be changed by automation, open `Settings` → `Rules` → `Rulesets` → `New branch ruleset` for `main` (or `Settings` → `Branches` on repositories using legacy protection). Require a pull request, require the four named checks above, require the branch to be up to date, and disallow force pushes and deletions.
