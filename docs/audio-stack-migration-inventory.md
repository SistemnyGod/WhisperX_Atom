# Audio Stack Migration Inventory

Status: implementation inventory for the controlled AudioGraph migration.

`LEGACY_WASAPI` remains the compatibility microphone fallback. The current-user
Recorder Host uses AudioGraph for the room microphone and an isolated NAudio
render-loopback adapter for the optional `system-audio` track; the two sources
never share a PCM writer.

| FILE | SYMBOL | CATEGORY | OLD_DEPENDENCY | NEW_OWNER | ACTION | GATE |
|---|---|---|---|---|---|---|
| `apps/recorder-agent/RecordingCoordinator.cs` | `StartAsync`, `CaptureTrack` | microphone, lifecycle | `MMDeviceEnumerator`, `WasapiCapture`, `IWaveIn`, `WaveFormat` | `IAudioCaptureEngineFactory` + neutral frame writer | REPLACE | A–E |
| `apps/recorder-host/SystemAudioCaptureEngine.cs` | system track creation | system loopback | `WasapiLoopbackCapture`, `MMDeviceEnumerator` | independent current-user render-loopback source | KEEP/ADAPT | P2 |
| `apps/recorder-agent/DeviceHealth.cs` | `Collect` | discovery, health | `MMDeviceEnumerator`, `MMDevice` | `IAudioDeviceCatalog` + runtime probe | REPLACE | A–E |
| `apps/recorder-agent/DeviceHealthMonitor.cs` | watcher/fallback | discovery, health | NAudio endpoint notifications | catalog event stream | REPLACE | A–E |
| `apps/recorder-agent/AudioRuntimeProbe.cs` | source probe | diagnostics | `WasapiCapture`, `WasapiLoopbackCapture` | `IAudioDeviceProbe` implementations | ADAPT | A–E |
| `apps/recorder-agent/AudioRuntimeProbeRunner.cs` | probe runner | diagnostics | legacy probe assumptions | engine-selected probe via IPC | REPLACE | A–E |
| `apps/recorder-agent/AudioSampleFormat.cs` | resolver | format | `WaveFormatExtensible`, `WaveFormatEncoding` | `AudioStreamFormat` + legacy adapter | ADAPT | H |
| `apps/recorder-agent/FlacEncoder.cs` | `Encode`, `RawFormat` | encoding | `WaveFormat`, FFmpeg path | `IAudioChunkEncoder`, `AudioStreamFormat` | ADAPT | H |
| `apps/recorder-agent/LegacyWasapiCaptureEngine.cs` | legacy engine/catalog | microphone, discovery | NAudio WASAPI/MMDevice | isolated fallback adapter | KEEP | A–E |
| `apps/recorder-agent/SpoolStore.cs` | chunk/track metadata | local-first | legacy encoding fields | neutral format metadata | ADAPT | A |
| `apps/recorder-agent/RawChunkRecovery.cs` | recovery | local-first | FFmpeg/legacy format assumptions | neutral format + encoder boundary | ADAPT | A/H |
| `apps/recorder-agent/LocalArchiveWriter.cs` | archive assembly | encoding | FFmpeg-produced tracks | neutral track metadata | ADAPT | H |
| `apps/recorder-agent/AgentPipeHost.cs` | commands/health | IPC | v5 device and Service assumptions | v6 versioned Host/Service protocol | ADAPT | A–E |
| `apps/recorder-agent/AgentIpcProtocol.cs` | DTOs | IPC | Service-shaped health DTO | neutral runtime/device DTOs | ADAPT | A–E |
| `apps/recorder-host/AudioGraphDeviceCatalog.cs` | watcher | discovery | none | `DeviceWatcher`, `DeviceInformation`, `MediaDevice` | KEEP | A |
| `apps/recorder-host/AudioGraphCaptureEngine.cs` | graph capture | microphone, telemetry | none | AudioGraph/input/output nodes | ADAPT | A |
| `apps/recorder-host/RecorderHostRuntime.cs` | two-track lifecycle | track orchestration | separate frame channels/writers | room + system independent durable tracks | ADAPT | P2 |
| `apps/recorder-host/RecorderHostRuntime.cs` | session writer/runtime | lifecycle, local-first | duplicated writer and no lease | shared neutral writer + lease/recovery | REPLACE | A–E |
| `apps/recorder-host/Program.cs` | host composition | lifecycle | no singleton/lease | Host runtime ownership | ADAPT | C/D/G |
| `apps/desktop/WhisperX.Atom.Desktop/AgentPipeClient.cs` | `SendAsync` | IPC | implicit pipe/version | explicit v6 negotiation | ADAPT | A–E |
| `apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs` | device refresh/selection | UI | health polling as device source | device snapshot/events + neutral DTOs | ADAPT | B |
| `apps/desktop/WhisperX.Atom.Desktop/Services/RecorderServiceController.cs` | startup | lifecycle | SCM-first startup | idempotent current-user Host startup | ADAPT | C/D/G |
| `apps/desktop/WhisperX.Atom.Desktop/Services/AgentBootstrapCoordinator.cs` | bootstrap | runtime state | Service readiness | Host/engine readiness | ADAPT | A–E |
| `apps/desktop/WhisperX.Atom.Desktop/Services/ClientRuntimeDiagnostics.cs` | diagnostics | diagnostics | Service/WASAPI labels | neutral engine/Host fields | ADAPT | A–E |
| `apps/desktop/Installer/Install-Service.ps1` | Service installer | installer | LocalSystem/allowed SID | Phase 1 fallback installer | KEEP | G |
| `scripts/install-recorder-host.ps1` | Host install | installer | no V2 config/lease | Host + user config + ACL | ADAPT | C/G |
| `scripts/start-recorder-host.ps1` | Host startup | lifecycle | no single instance | mutex/lease-aware startup | ADAPT | C/D/G |
| `scripts/probe-audio-runtime.ps1` | probe | diagnostics | legacy-only assumptions | selected engine via IPC | REPLACE | A–E |
| `scripts/acceptance-local-recording.ps1` | local gate | acceptance | Service/legacy default | `-CaptureEngine` and engine report | ADAPT | A |
| `tests/test_recorder_lan_vertical_contracts.py` | legacy contracts | tests | implementation details | behavior tests + isolated legacy tests | ADAPT | A–H |
| `tests/test_audiograph_migration_contracts.py` | AudioGraph contracts | tests | new Host assumptions | runtime boundary contracts | ADAPT | A–E |
| `apps/voice-host/WhisperX.Atom.Voice.Host/VoiceAudioCapture.cs` | voice capture | optional subsystem | NAudio | unrelated optional Voice Host | KEEP | out of scope |
| `workers/media_worker/*` | media processing | server encoding | FFmpeg server worker | server media pipeline | KEEP | out of scope |
| `docs/architecture-current.md` | architecture claims | documentation | Service/WASAPI primary claims | staged runtime model | ADAPT | after switch |
| `docs/recorder_agent.md` | operations | documentation | Service-only assumptions | Host + fallback model | ADAPT | after switch |
| `docs/configuration.md` | env/config | documentation | legacy env only | KEEP/LEGACY/REMOVE_LATER matrix | ADAPT | after switch |

## Configuration classification

| Configuration | Status | Reason |
|---|---|---|
| `AUDIO_CAPTURE_ENGINE` | KEEP | release switch and A/B test control |
| `ATOM_AGENT_FFMPEG_PATH` / `ATOM_AGENT_FFPROBE_PATH` | LEGACY | retained until native encoder gate H |
| `ATOM_AGENT_ALLOWED_SID` / `allowed-user.sid` | LEGACY | Service pipe and ACL fallback until gate G |
| `ATOM_AGENT_DPAPI_SCOPE` | KEEP | selects LocalMachine for Service and CurrentUser for Host |
| `WhisperXAtomAgent` pipe | LEGACY | v5 Service fallback |
| `WhisperXAtomRecorderHost` pipe | KEEP | current-user Host production candidate |

## Remaining-reference policy

After gates A–E, the inventory is regenerated. Any remaining microphone-path NAudio reference must be either in the isolated legacy adapter or in this table with an explicit gate. No release flag is set to `true` merely because an AudioGraph class exists or a project builds.
