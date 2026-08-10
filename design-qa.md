# Design QA

Reference: WhisperX Atom Desktop Control Room, white/light shell with cobalt-blue primary actions.
Target viewport: 1440x900, 1586x992 and 1920x1080.

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
- the header now keeps a neutral system label while API and Recorder Agent details stay in their operational areas;
- the left navigation is now fixed to the reference's wider control-room rail;
- recording controls are state-aware and only show actions relevant to idle, recording or paused mode.
- the recording eyebrow now communicates the actual mode: readiness, active recording, pause, or unavailable service;
- the quick-actions rail routes usable actions into the existing workflows and keeps calendar/export disabled until integrations exist.
- the selected recording source area now opens Sources, and the agent card exposes a direct Settings action.
- hidden demo KPI, meeting and notification blocks were removed; recent meetings render only API data.
- the recording timer renders only the Recorder Agent `MediaTimeMs` value and stays empty when no session is active.
- the recent-meetings table now uses compact date, title, status, description and open-action columns.
- the recording action stack remains visible in idle/offline states with honest disabled controls, matching the reference geometry;
- the idle state hides the timer until an active Recorder Agent session exists;
- the left rail was widened slightly so the Home composition reads as a control-room layout rather than a compact legacy form.

Latest full-shell polish:

- the application now uses one cobalt-blue control-room token set across the shell, cards, fields, tabs, progress bars and disabled states;
- the outer shell has more deliberate breathing room, a wider navigation rail, a taller header and a borderless content frame for a calmer desktop composition;
- card geometry is now consistent across Home, Meetings, Sources, Tasks, Assistant and Settings with larger radii, tighter borders and stronger surface hierarchy;
- the recording hero uses the shared blue surface, larger title scale and a more deliberate action column while retaining the existing Idle, Recording, Paused and Unavailable behavior;
- KPI icon surfaces, meeting status badges, empty-state icon surfaces and assistant response surfaces now share the same semantic blue, green, purple and orange palette;
- the header system indicator remains neutral and compact, while the agent state continues to be reported in the operational areas rather than overloaded into the shell;
- no mock meetings, people, GPU values or notifications were added; all data continues to come from the existing API and IPC clients.
- button styles now use explicit text content templates for standard, primary, danger and ghost actions; this fixes the low-contrast dark labels previously visible on cobalt primary actions and keeps disabled labels readable.
- button content templates explicitly reset the global TextBlock margin; the Home empty-state actions use fixed sizes, spacing and z-order so the lower buttons do not visually collide.
- hover and pressed states are now owned by each button style; the shared control template no longer replaces a primary/danger background with a pale surface, and disabled buttons ignore hover styling.

## Result

final result: blocked

The WPF build and runtime launch were verified, but this environment cannot capture the Windows surface reliably: screen capture returns an invalid desktop handle and the Computer Use helper cannot start. The supplied user screenshot was used for layout review; a fresh post-change screenshot still requires a working Windows capture path.

## Automated checks

- `dotnet build apps/desktop/WhisperX.Atom.Desktop/WhisperX.Atom.Desktop.csproj --no-restore --nologo` — passed, 0 warnings/errors.
- Desktop process after launch — confirmed running and responsive.
- `git diff --check` — passed; only line-ending warnings remain.
- Latest build after the shell pass — passed, 0 warnings/errors.

## Follow-up

When Windows capture is available, verify the Home screen at 1440x900 and 1586x993, then continue the same visual system through Meetings and the selected-meeting workspace.
