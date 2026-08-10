# Design QA

Reference: WhisperX Atom Desktop Control Room, white/light shell with cobalt-blue primary actions.
Target viewport: 1440x900 and 1586x993.

## Current pass

The Desktop Home screen now includes:

- a light control-room shell with left navigation, search, system status and profile area;
- a recording-first hero with real Recorder Agent state and disabled controls until the service is ready;
- real meeting rows loaded from `GET /api/meetings`;
- dashboard counters populated from Recorder Agent/API data, with `—` when the API does not expose a metric;
- real disk capacity from the agent health response;
- explicit empty states instead of hardcoded meeting, GPU, CPU, memory and notification values;
- retained functional controls for recording and file import.

Latest Home polish:

- idle state collapses processing details until there is an active recording or job;
- recording, pause and unavailable states use distinct indicator colors and synchronized labels;
- the recording marker action now has a real handler;
- the quick action for a new meeting focuses the recording title field;
- calendar/series/export actions are visibly disabled until their integrations exist;
- the desktop window title and card spacing now align more closely with the selected reference.
- dashboard metrics now use colored icon circles matching the control-room visual language;
- the empty recent-meetings state now explains the next step and exposes working refresh/import actions.
- the global search field is now interactive, supports `Ctrl + K`, and filters the meetings registry;
- recent-meeting rows and the «Открыть все совещания» action now open the working meetings workspace;
- the backend status chip now changes its surface and border treatment when the API is unavailable.
- the left navigation is now fixed to the reference's wider control-room rail;
- recording controls are state-aware and only show actions relevant to idle, recording or paused mode.
- the recording eyebrow now communicates the actual mode: readiness, active recording, pause, or unavailable service;
- the quick-actions rail now contains only usable actions and routes connection setup into Settings instead of showing dead disabled controls.

## Result

final result: blocked

The WPF build and runtime launch were verified, but this environment cannot capture the Windows surface reliably: screen capture returns an invalid desktop handle and the Computer Use helper cannot start. The supplied user screenshot was used for layout review; a fresh post-change screenshot still requires a working Windows capture path.

## Automated checks

- `dotnet build apps/desktop/WhisperX.Atom.Desktop/WhisperX.Atom.Desktop.csproj --no-restore --nologo` — passed, 0 warnings/errors.
- Desktop process after launch — confirmed running and responsive.
- `git diff --check` — passed; only line-ending warnings remain.

## Follow-up

When Windows capture is available, verify the Home screen at 1440x900 and 1586x993, then continue the same visual system through Meetings and the selected-meeting workspace.
