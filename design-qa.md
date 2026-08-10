# Design QA

Reference: WhisperX Atom Desktop Control Room — светлый shell, cobalt-blue primary actions, тонкие границы и плотная рабочая область.

Target viewports: 1180x720, 1586x992 and 1920x1080.

## First WinUI 3 increment

Implemented:

- WinUI 3 unpackaged self-contained client on .NET 10 / Windows App SDK 2.3.1;
- stable three-row shell with native title bar, compact footer, Mica backdrop and NavigationView;
- working routes: Главная, Запись, Настройки;
- real Recorder Agent health polling every two seconds on the recording page;
- real API readiness and meeting loading through the existing backend client;
- device selectors for microphone and system audio, synchronized through IPC;
- local archive root selection and persistence in the existing desktop settings;
- recording actions START, PAUSE, RESUME, MARKER and STOP;
- timer sourced only from `AgentIpcResponse.MediaTimeMs`;
- explicit empty, unavailable and error states without demo meetings, GPU values, people or notifications;
- directory self-contained publish with `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true` and `PublishSingleFile=false`;
- Inno Setup continues to consume `artifacts/desktop/Desktop` and keeps the existing service/Voice Host payload.

## Automated checks

- `dotnet build apps/desktop/WhisperX.Atom.Desktop/WhisperX.Atom.Desktop.csproj --configuration Debug --no-restore --nologo` — passed, 0 warnings/errors.
- self-contained WinUI publish to `artifacts/desktop/Desktop-WinUI-ControlRoom3` — passed.
- Release Desktop publish to `artifacts/desktop/Desktop` — passed after NuGet restore; published process remained responsive and the PRI resource file was present.
- published WinUI process — started and remained responsive during the smoke interval.
- `WhisperX.Atom.Desktop.pri` generated in the published output, so app-specific XAML resources are present.
- `git diff --check` — run before handoff; line-ending-only warnings are acceptable for existing files.

## Manual QA status

Blocked in the current environment: a reliable fresh screenshot/control path for the native Windows surface is unavailable. The process smoke test passed, but visual comparison and click-through of all three routes must be repeated on a normal Windows desktop at the target viewports.

The complete installer publish remains partially blocked by the local .NET SDK workload locator error while publishing the existing Voice Host project. The WinUI Desktop and Recorder Service payloads publish successfully; this is an environment/toolchain issue outside the WinUI client changes and must be resolved before producing a final Inno executable.

Required manual scenarios:

- Agent/API available and unavailable;
- microphone and system-audio selection;
- start, pause, resume, marker and stop;
- local archive persistence and pending-upload state after restart;
- API login, Agent enrollment and archive-folder settings;
- no overlap at 1180x720, 1586x992 and 1920x1080;
- Defender/endpoint-protection behavior for the unpackaged directory payload.

## Meetings increment

Implemented:

- WinUI route `meetings` with a real API-backed meetings list;
- client-side search by title and description, refresh and file import;
- split list/workspace layout with a compact vertical fallback below the shared 1200px wide breakpoint;
- meeting overview, transcript, summary, decisions, tasks and files tabs;
- existing job retry, summary rebuild, task status update and preview download contracts;
- cancellation of page and stale workspace requests when navigating or selecting another meeting;
- no production mocks, demo meetings, fake metrics or synthetic files.

Manual visual QA for the new route remains `blocked` until a reliable native WinUI screenshot/control path is available. The desktop build is the automated gate; click-through must be repeated at 1180x720, 1586x992 and 1920x1080 on a normal Windows desktop.

## Tasks and Assistant increment

Implemented:

- WinUI routes `tasks` and `assistant` with real API-backed data only;
- paginated meeting loading and a maximum of four concurrent task requests for the global registry;
- active/all status filters, deadline and responsible filters, client-side search and overdue-first ordering;
- task editing through the existing `UpdateTaskAsync` contract, with local state updated only after a successful API response;
- responsive task layout: list plus editor on wide windows and vertical list/editor layout below the shared 1200px wide breakpoint;
- assistant context selection from real meetings and privileged whole-history mode based on `/api/auth/me`;
- cancellable assistant polling every two seconds with a four-minute limit and explicit queued, running, ready, review, failed and timeout states;
- evidence items preserve legacy responses without `meetingId`, while source navigation is disabled with an explanation for those items;
- evidence navigation opens the meeting workspace, transcript tab, matching segment and real preview when available;
- assistant worker and legacy API fallback now include `meetingId` in evidence without changing the assistant endpoint contract.

Automated validation:

- `dotnet build apps/desktop/WhisperX.Atom.Desktop/WhisperX.Atom.Desktop.csproj --configuration Debug --no-restore --nologo` — passed, 0 warnings/errors;
- `dotnet build apps/desktop/WhisperX.Atom.Desktop/WhisperX.Atom.Desktop.csproj --configuration Release --no-restore --nologo` — passed, 0 warnings/errors;
- `dotnet build apps/server/WhisperX.Atom.Api/WhisperX.Atom.Api.csproj --configuration Debug --no-restore --nologo` — passed, 0 warnings/errors;
- `py -m py_compile workers/summary_worker/assistant.py` — passed;
- `git diff --check` — passed; only existing line-ending normalization warnings were reported by Git;
- Native WinUI screenshot/control QA remains `blocked` until a reliable capture path is available; target viewports remain 1180x720, 1586x992 and 1920x1080.

## Sources increment

Implemented:

- WinUI route `sources` in the existing Control Room shell;
- local Recorder Agent status, current recording state, archive root, disk capacity and pending-upload count from IPC `HEALTH`;
- real microphone and system-audio device lists, including the selected/default source and device state;
- registered Recorder Agents loaded through the existing authenticated `/api/agents` endpoint;
- independent error handling for local IPC and backend API, so one available source remains usable when the other fails;
- responsive two-column layout with a vertical fallback below the shared 1200px wide breakpoint;
- direct navigation to Recording and Settings without introducing new server or IPC contracts;
- no production mocks, synthetic devices, fake agent metrics or web-panel dependencies.

## UI stabilization increment

Implemented:

- shared typography and layout resources for page padding, gaps and compact/standard/wide breakpoints;
- explicit WinUI button templates for normal, hover, pressed and disabled states so button labels remain readable;
- shared semantic colors for hero borders, table headers, neutral status and danger surfaces;
- responsive hero/KPI/settings layouts and unified split-layout threshold for Home, Recording, Settings, Meetings, Tasks, Assistant and Sources;
- explicit focus visuals and danger-button hover/pressed states in addition to normal, hover, pressed and disabled states;
- responsive recording/API action stacks and task filters so controls no longer overlap on compact and standard widths;
- horizontal scrolling disabled on page roots and fixed table columns reduced where needed for compact content widths;
- Russian shell labels and shared page-title/hero/metric typography tokens;
- form controls on all routes now share the same ComboBox, PasswordBox, DatePicker, CheckBox and workspace-tab styles;
- Home refresh/import requests are cancellable, the Agent indicator uses the real health result, and the meetings shortcut opens Совещания;
- Home now uses a wide Control Room composition with the main recording/meetings column and a right operational rail for quick actions, real Agent state and an honest notifications empty-state;
- the common shell now has a compact footer and a real combined API/Recorder Agent status indicator, refreshed periodically and cancelled on window close;
- Recording exposes a retry-upload action only when a failed local session has a session id, while preserving the local archive;
- Recording now separates the left state/lifecycle workspace from a right vertical control, device and archive rail; state indicator colors follow real Idle, Recording, Paused, Finalizing and Error states;
- meeting workspace data is cleared when selection or refresh changes, preventing stale details from the previous meeting;
- Meetings now expose the selected meeting state in a compact status badge and cover the workspace with an explicit loading state while API details are refreshed;
- Meeting list rows keep a small visual separation so the selection target remains readable in the dense list without changing the real-data flow;
- Settings now uses the shared status-badge token, keeps Agent registration disabled until API login succeeds, and stacks archive-folder actions vertically in compact mode;
- Sources now presents local Agent and backend availability as shared status badges, with tighter server-agent table columns for standard and compact widths;
- encoding guard script at `scripts/check-desktop-ui-encoding.ps1` for runtime XAML/C# files.
- StaticResource guard at `scripts/check-desktop-ui-resources.ps1` for runtime XAML resource references.

Automated validation for this stabilization increment:

- Debug Desktop build — passed, 0 warnings/errors;
- Release Desktop build — passed, 0 warnings/errors;
- UTF-8/mojibake guard — passed, 147 runtime files checked;
- StaticResource guard — passed, all runtime XAML references resolve to declared resources;
- `git diff --check` — passed; only existing LF/CRLF normalization warnings were reported by Git;
- native WinUI screenshot/control QA remains `blocked` because a reliable fresh capture path is unavailable in this environment. Manual verification is still required at 1180x720, 1586x992 and 1920x1080, including mouse/keyboard focus and disabled states.
