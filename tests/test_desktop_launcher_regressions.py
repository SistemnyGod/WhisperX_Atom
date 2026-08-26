from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_launcher_does_not_read_unset_last_exit_code_after_powershell_script():
    launcher = (ROOT / "scripts" / "launch-desktop.ps1").read_text(encoding="utf-8")
    assert "if (-not $?)" in launcher
    assert 'if ($LASTEXITCODE -ne 0) { throw "RECORDER_HOST_START_FAILED' not in launcher
    assert 'if ($LASTEXITCODE -ne 0) { throw "RECORDER_SERVICE_START_FAILED' not in launcher
    assert "Get-Variable LASTEXITCODE" in launcher


def test_recorder_host_launcher_handles_unset_last_exit_code():
    launcher = (ROOT / "scripts" / "start-recorder-host.ps1").read_text(encoding="utf-8")
    assert "Get-Variable LASTEXITCODE" in launcher
    assert 'throw "RECORDER_HOST_BUILD_FAILED: exit code $publishExitCode"' in launcher


def test_responsive_layout_guards_transient_xaml_tree():
    layout = (ROOT / "apps" / "desktop" / "WhisperX.Atom.Desktop" / "Services" / "ResponsiveLayout.cs").read_text(encoding="utf-8")
    assert "if (grid is null || first is null || second is null) return;" in layout
    assert "while (grid.ColumnDefinitions.Count < 2)" in layout


def test_host_launcher_does_not_spawn_duplicate_process_when_ipc_is_temporarily_unready():
    launcher = (ROOT / "scripts" / "start-recorder-host.ps1").read_text(encoding="utf-8")
    assert "No duplicate Host was started" in launcher
    assert 'if ($anyHosts.Count -gt 0) {' in launcher
    assert 'throw "RECORDER_HOST_PIPE_UNRESPONSIVE' in launcher


def test_desktop_never_kills_host_without_verified_executable_path():
    controller = (ROOT / "apps" / "desktop" / "WhisperX.Atom.Desktop" / "Services" / "RecorderServiceController.cs").read_text(encoding="utf-8")
    assert "string.IsNullOrWhiteSpace(expectedPath)" in controller
    assert "string.IsNullOrWhiteSpace(actualPath)" in controller
    assert "|| string.IsNullOrWhiteSpace(actualPath)" in controller
