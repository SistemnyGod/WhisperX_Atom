# WhisperX Atom Desktop

The product UI is a Windows desktop application. The browser UI is not required.

- `WhisperX.Atom.Desktop`: WPF operator and administration UI.
- `WhisperX.Atom.Recorder.Service`: Windows Service for capture, local spool and upload.
- Internal API/Docker workers remain on the local GPU server (default: `http://localhost:8080`).
- `AgentPipeHost` exposes a local Named Pipe (`WhisperXAtomAgent`) for desktop recording controls.

Build a package:

```powershell
.\scripts\publish-desktop.ps1
```

The Inno Setup file is `apps/desktop/Installer/WhisperXAtom.iss`. It installs the Desktop UI and registers the Recorder Service.

The browser `web`/`gateway` services are optional diagnostics only; normal Desktop runtime uses the API port directly.

## Launching from Windows

From the repository folder, double-click `run_app.bat` or `start_whisperx_gui.bat`. Both launch the WPF Desktop application through `scripts\launch-desktop.ps1`; they do not require the obsolete root `.venv` or a specific current working directory.

The launcher checks the local Debug/Release output and the published package, and builds the Desktop project with `dotnet` when no executable is present. To select an explicit executable, set `WHISPERX_DESKTOP_EXE` before launch.
# Building the Windows installer

1. Run `scripts\\publish-desktop.ps1` to create self-contained Desktop and Service binaries.
2. Install Inno Setup 6 on the build machine.
3. Run `scripts\\build-installer.ps1`.
4. The `.exe` installer is written to `artifacts\\installer`.

The installer registers `WhisperXAtomRecorder` as an automatic Windows Service and creates a Desktop shortcut. The internal Docker/API backend is configured separately; the Desktop default API endpoint is `http://localhost:8080`.

The Windows host must have FFmpeg on PATH. The Recorder Service uses it to encode local FLAC chunks; the installer checks this requirement during service registration.

Set `AGENT_ENROLLMENT_SECRET` in `.env` before starting the local Compose profile; the stack has no built-in enrollment fallback.

## Local acceptance and backup

Run the Desktop-only acceptance checks before a pilot:

    .\scripts\acceptance-desktop.ps1

Use -SkipDocker or -SkipGpu for UI-only validation. The script verifies Desktop/Service builds, Python tests, Compose configuration, core service health, GPU runtime, installer presence, and encoding artefacts.

Create a database/configuration backup without copying media:

    .\scripts\backup.ps1

The backup contains a PostgreSQL custom dump, compose.dev.yml, .env.example, the WSL2 runbook and a SHA-256 manifest. Add -IncludeMedia only when an archive copy is required. The real .env and its secrets are never included.
