from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_server_exposes_anonymous_update_manifest_and_range_package():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert 'app.MapGet("/api/client-updates/latest"' in api
    assert 'app.MapGet("/api/client-updates/packages/{packageId}"' in api
    assert "enableRangeProcessing: true" in api
    assert "Path.GetFileName(fileName)" in api
    assert 'context.Request.Path.StartsWithSegments("/api/client-updates")' in api


def test_publish_script_is_atomic_and_rejects_dev_or_dirty_identity():
    script = read("scripts/publish-client-update.ps1")
    assert "buildIdentity -notmatch" in script
    assert "dev|dirty" in script
    assert ".exe.part" in script
    assert "client-update.json.part" in script
    assert "Move-Item -LiteralPath $manifestPart" in script
    assert "$Channel -eq 'stable' -and $signature.Status -ne 'Valid'" in script


def test_desktop_update_service_checks_before_auth_and_blocks_active_recording():
    service = read("apps/desktop/WhisperX.Atom.Desktop/Services/ClientUpdateService.cs")
    app = read("apps/desktop/WhisperX.Atom.Desktop/App.xaml.cs")
    assert "ClientUpdateState" in service
    assert "UPDATE_BLOCKED_ACTIVE_RECORDING" in service
    assert "STARTING" in service and "FINALIZING" in service
    assert "StartMonitoring()" in app
    assert "currentBuildIdentity" in service
    assert "UPDATE_PACKAGE_CHECKSUM_MISMATCH" in service
    assert "UPDATE_UNSIGNED_PILOT_CONFIRMATION_REQUIRED" in service
    assert "InstallAsync(bool allowUnsignedPilot" in service


def test_updater_never_forces_reboot_and_installer_carries_helper():
    updater = read("apps/desktop/Updater/Program.cs")
    iss = read("apps/desktop/Installer/WhisperXAtom.iss")
    publish = read("scripts/publish-desktop.ps1")
    assert "/NORESTART" in updater
    assert "WaitForExitAsync" in updater
    assert "expected-sha256" in updater
    assert "UPDATER_POST_INSTALL_IDENTITY_MISMATCH" in updater
    assert "UPDATER_ROLLBACK_SNAPSHOT_FAILED" in updater
    assert "TryRestoreDirectory" in updater
    assert "WhisperX.Atom.Updater.exe" in publish
    # Desktop wildcard payload includes the helper without adding a second
    # installer source that could diverge from the published identity.
    assert 'Source: "..\\..\\..\\artifacts\\desktop\\Desktop\\*"' in iss


def test_installer_release_manifest_rejects_dirty_or_mixed_runtime():
    script = read("scripts/build-installer.ps1")
    assert "INSTALLER_RELEASE_IDENTITY_INVALID" in script
    assert "INSTALLER_RUNTIME_IDENTITY_MISMATCH" in script
    assert "identity.dirty" in script
