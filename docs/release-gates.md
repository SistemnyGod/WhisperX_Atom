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
- microphone and system-audio device-loss recovery;
- low-disk recording protection;
- cold model-cache and GPU-OOM recovery;
- delete-locked-media recovery;
- 30-minute, 2-hour, 4-hour and 8-hour endurance;
- backup/restore acceptance;
- RBAC isolation.

Set `MVP_V1_READY=true` only after every required record is available. The
release script is fail-closed: a missing physical/hardware artifact blocks the
release even when all .NET/Python CI checks are green. Create a release tag
only after the protected `main` merge is green.

Acceptance artifacts live below `artifacts/acceptance/<scenario>/` and must be
JSON with `status` (or `result`) equal to `READY`, `PASSED` or `GREEN` (or
`passed: true`). They must contain metrics and IDs only; do not place audio,
transcript text, credentials or tokens in the reports. The required scenario
names are the canonical names used by `scripts/release-gate.ps1`:

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
audio-device-loss
system-audio-device-loss
low-disk-during-recording
cold-model-cache
gpu-oom
delete-locked-media
endurance-30m
endurance-2h
4h-recording
8h-recording
backup-restore
rbac-isolation
```

If branch protection cannot be changed by automation, open `Settings` → `Rules` → `Rulesets` → `New branch ruleset` for `main` (or `Settings` → `Branches` on repositories using legacy protection). Require a pull request, require the four named checks above, require the branch to be up to date, and disallow force pushes and deletions.
