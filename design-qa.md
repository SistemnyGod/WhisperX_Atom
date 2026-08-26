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

## Reference-aligned Home refinement

Source visual truth: `C:\Users\AI_SER~1\AppData\Local\Temp\codex-clipboard-06e035e3-a35d-48c1-8be4-dcde82f2472b.png`.

Implemented:

- the Home hero now follows the reference hierarchy with a compact recording-state badge, a right-aligned duration and the existing local archive/upload status row;
- `LIVE`, pause, finalizing, error and unavailable labels are derived from the real Recorder Agent IPC state;
- the duration is rendered only from `AgentIpcResponse.MediaTimeMs`; no timer, signal meter or GPU metric is synthesized;
- existing routes are visually grouped in the NavigationView with `РАБОТА`, `ДАННЫЕ` and `СИСТЕМА` headers without adding routes;
- the right operational rail remains honest: quick actions, real Agent/archive state and the notification empty-state only.

Automated validation for this refinement:

- Debug Desktop build — passed, 0 warnings/errors;
- Release Desktop build — passed, 0 warnings/errors;
- UTF-8/mojibake guard — passed, 147 runtime files checked;
- StaticResource guard — passed, all runtime XAML references resolve;
- `git diff --check` — passed; only existing LF/CRLF normalization warnings were reported by Git.

## Visual QA for the reference-aligned refinement

Implementation screenshot: unavailable; native WinUI capture/control path is not exposed in the current environment.

Viewport: target comparison remains 1180x720, 1586x992 and 1920x1080. Source and implementation pixel dimensions/density cannot be normalized because the implementation screenshot is unavailable. State to verify on a normal Windows desktop: Home with Agent available, Home with Agent unavailable, active recording with a real `MediaTimeMs`, and idle recording state.

Full-view and focused-region comparison: blocked before comparison because the native implementation artifact cannot be captured. The focused regions to verify are the shell navigation grouping, Home hero badge/timer, right operational rail, KPI strip and recent-meetings empty/list state.

Findings: no automated layout or encoding regressions detected. Native visual verification and keyboard/mouse state checks remain open.

Previous iteration result: blocked

## Current plan execution increment

Implemented:

- Recording now uses the same compact state-badge pattern as Home, with real `RecordingState` colors for recording, paused, finalizing, error, unavailable and idle states;
- repeated caption sizes on Home, Recording, Meetings, Tasks, Assistant, Sources and Settings now reference the shared `CaptionTextSize` token;
- no new routes, REST/IPC methods, demo values or synthetic device/processing data were introduced.
- Home now polls only Recorder Agent health every two seconds, with linked cancellation on navigation; API readiness and meetings are refreshed on entry, manual refresh and import instead of on every timer tick.

Validation:

- Debug Desktop build — passed, 0 warnings/errors;
- Release Desktop build — passed, 0 warnings/errors;
- UTF-8/mojibake guard — passed, 147 runtime files checked;
- StaticResource guard — passed, all runtime XAML references resolve;
- native visual comparison remains blocked because a fresh native WinUI implementation screenshot is unavailable.

Runtime smoke checks:

- `GET http://localhost:8080/health` — HTTP 200, API reports `ok: true`;
- `GET /api/meetings?limit=1&offset=0` without a session — HTTP 401, authentication boundary is active;
- `\\.\pipe\WhisperXAtomAgent` is present; direct HEALTH invocation from the current PowerShell identity was denied by the pipe ACL and must be verified through the installed Desktop/service security context.

## Next route implementation increment

Implemented:

- \`Агенты\` route backed by the existing \`/api/agents\` contract and Recorder Agent IPC health;
- \`Стенограммы\` route backed by the existing paged meetings list and \`/api/meetings/{id}/transcript\`;
- both routes use linked page cancellation, loading/empty/error/partial-warning states and responsive compact/standard/wide layouts;
- transcript evidence opens the existing meeting workspace with the selected segment and real start time;
- no new endpoint, production mock or server model was added;
- \`Серии оперативок\`, \`Спикеры\`, \`Саммари\` and administration actions remain outside this increment where no complete dedicated contract exists.

Validation:

- Debug Desktop build — passed, 0 warnings/errors;
- Release Desktop build — passed, 0 warnings/errors;
- UTF-8/mojibake guard — passed, 165 runtime files checked;
- StaticResource guard — passed, 15 runtime XAML files checked;
- \`git diff --check\` — passed;
- native WinUI visual comparison remains blocked because a fresh native implementation screenshot is unavailable.

## Current route implementation increment

Implemented:

- Speakers route backed by the existing meetings page contract and /api/meetings/{id}/speakers;
- Summaries route backed by the existing meetings page contract and /api/meetings/{id}/summary;
- summary rebuild uses the existing RebuildSummaryAsync contract and reloads the server response before updating the selected row;
- both routes use four-request concurrency limits, linked page cancellation, loading/empty/error/partial-warning states and compact/standard/wide layouts;
- no new endpoint, server model, production mock or fabricated aggregate was added;
- Series and full administration remain outside the increment because no complete dedicated contract is available.

Validation:

- Debug Desktop build — passed, 0 warnings/errors;
- Release Desktop build — passed, 0 warnings/errors;
- UTF-8/mojibake guard — passed, 183 runtime files checked;
- StaticResource guard — passed, 17 runtime XAML files checked;
- git diff --check — passed;
- native WinUI visual comparison remains blocked because a fresh native implementation screenshot is unavailable.

## Acceptance and packaging checks

- API health/readiness — passed: `/health` and `/ready` returned 200;
- protected API boundary — passed: meetings, agents and assistant queries returned 401 without a session;
- cancellation and concurrency — passed: page cancellation is wired and aggregate loaders are limited to four parallel requests;
- production mock scan — passed: no Mock/Demo/Sample/Fake markers in runtime Desktop files;
- Debug and Release Desktop builds — passed, 0 warnings/errors;
- targeted Python acceptance rerun — passed: 82 tests, 56 skipped, 0 errors;
- sequential Debug and Release builds for Desktop, Recorder Service and Voice Host — passed, 0 warnings/errors;
- Desktop, Recorder Service and Voice Host publish — passed; Vosk model/native smoke passed;
- fresh Inno Setup installer build — passed; artifact SHA256 `3EAEF03F8E12D2F7170590901EA19D43C7B66C8D9DE449C50F70B2328C859339`;
- Voice Host publish uses `MSBuildEnableWorkloadResolver=false` because the installed SDK image lacks workload resolver locator SDKs;
- native WinUI visual QA — blocked because a fresh native implementation screenshot is unavailable.

final result: blocked

## Registry and system-status refinement — 2026-08-18

Source visual truth:

- `C:\Users\AI_SER~1\AppData\Local\Temp\codex-clipboard-e915fa43-3210-4d57-a2f5-58743c53931a.png` — поручения;
- `C:\Users\AI_SER~1\AppData\Local\Temp\codex-clipboard-fcc701a8-7f8e-4f31-9fe8-92351f0ab8eb.png` — спикеры;
- `C:\Users\AI_SER~1\AppData\Local\Temp\codex-clipboard-1cdb56d2-1391-4a5c-b66d-e5eab6d8e922.png` — состояние системы.

Implemented:

- task filters now share one bounded card and reflow through the common compact/standard/wide breakpoints;
- the task registry has explicit readable columns while meeting and responsible values trim with full-value tooltips;
- compact task and speaker layouts no longer reserve an empty detail card before a row is selected;
- speaker identifiers and long meeting names stay on stable lines and expose the full value through tooltips;
- the system page uses the user-facing title `Состояние системы`, a four-card wide summary row, and a searchable connection registry;
- installation ID and heartbeat age are scoped to the selected-agent inspector instead of the main table;
- no server API, Recorder IPC, or Voice Host IPC contract changed.

Automated evidence:

- Desktop Release build passed with 0 warnings/errors;
- 40 Desktop UI contract tests passed;
- StaticResource validation passed for 26 XAML files;
- UTF-8 validation passed for 354 runtime files;
- scoped `git diff --check` reported no whitespace errors.

Full-view and focused-region comparison remain blocked because a fresh screenshot of the newly compiled native WinUI build is unavailable. The supplied screenshots are valid source references but represent the older installed build.

Remaining verification: capture Tasks, Speakers, and System Status at 1366×768 and 1920×1080 with 100–150% Windows scaling, then compare filter reflow, table density, inspector transitions, and long-value trimming.

final result: blocked

## Meeting reading workspace refinement — 2026-08-18

Source visual truth: `C:\Users\AI_SER~1\AppData\Local\Temp\codex-clipboard-3e519f95-c25e-4fb4-9a66-8b2d2a8f5cf6.png` (1674×941, light meeting workspace with transcript-first layout).

Implementation screenshot: unavailable for the freshly compiled native WinUI build. The screenshots supplied in the conversation represent an older installed build and are not valid post-change evidence.

Target viewport: 1366×768 and 1920×1080 at 100–150% Windows scaling. Pixel density normalization cannot be completed without a current native capture. State: opened meeting, transcript tab selected, transcript and processing jobs loaded.

Implemented from the source composition:

- transcript reading remains the dominant column and is limited to a comfortable text measure;
- export actions are consolidated into one menu while reprocessing remains visible;
- search/navigation and display options occupy two stable toolbar rows;
- the processing rail moves below the transcript below 1240 px instead of squeezing text;
- protocol decision/task cards stack at the compact breakpoint;
- summary warnings are scoped to the selected result rather than displayed as a duplicate page-wide banner;
- compact summary navigation uses list → result master/detail behavior.

Full-view comparison: blocked because a current implementation screenshot is unavailable.

Focused-region comparison: blocked for the transcript toolbar, processing rail, summary warning, and compact master/detail transition for the same reason.

Automated evidence:

- Desktop Release build passed with 0 warnings/errors;
- 37 Desktop UI contract tests passed;
- StaticResource validation passed for 26 XAML files;
- UTF-8 validation passed for 354 runtime files;
- `git diff --check` reported no whitespace errors.

Remaining P2 verification blocker: capture and compare the current native WinUI meeting workspace at the target viewports and scaling factors. No implementation failure was found by the automated gates.

Comparison history: the source showed a transcript-first workspace; the previous implementation exposed four competing header buttons and retained a side rail at notebook widths. This iteration consolidated exports, split the toolbar into stable rows, and introduced a responsive rail. Post-fix visual evidence is still unavailable.

final result: blocked

## Settings, sources, and sign-in refinement — 2026-08-18

Source visual truth:

- `C:\Users\AI_SER~1\AppData\Local\Temp\codex-clipboard-e5373963-f031-4724-ad50-379ab168fd4a.png` — dark sign-in screen;
- `C:\Users\AI_SER~1\AppData\Local\Temp\codex-clipboard-e3e8f2c5-ee05-4d92-9dbd-00c8f4ce4350.png` — settings overview;
- `C:\Users\AI_SER~1\AppData\Local\Temp\codex-clipboard-5f9f3f88-ff57-42d0-b48d-a4d1b1cc46c7.png` — Mifodiy settings.

Implemented:

- the sign-in form explicitly keeps placeholder and remember-login content legible on the dark surface;
- settings now expose direct navigation to Connection, Mifodiy, Recording and archive, and Diagnostics;
- diagnostics shortcuts expand the existing Recorder and Mifodiy diagnostic sections without introducing a new runtime contract;
- the settings section toolbar reflows vertically at the compact breakpoint;
- source cards use matched minimum heights and reserve stable one-line regions for microphone and archive values;
- raw endpoint identifiers were removed from the primary microphone/system-audio lists; human device names remain visible with full-name tooltips;
- the server connection registry was reduced to the three user-facing columns: name, status, and last contact.

Automated evidence:

- Desktop Release build passed with 0 warnings/errors;
- 43 Desktop UI contract tests passed;
- StaticResource validation passed for 26 XAML files;
- UTF-8 validation passed for 354 runtime files;
- scoped `git diff --check` reported no whitespace errors.

Full-view and focused-region comparison remain blocked because the current compiled native WinUI build has not been captured. The supplied screenshots show the previous installed build and cannot prove the post-change geometry.

Remaining verification: capture sign-in at 720×640 and 1080×760, then Settings and Sources at 1366×768 and 1920×1080 with 100–150% scaling. Compare checkbox/placeholder contrast, section navigation, Mifodiy card stability, device-name trimming, and stacked compact layouts.

final result: blocked
