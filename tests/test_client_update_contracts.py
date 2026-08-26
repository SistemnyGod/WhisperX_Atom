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
    assert "RECORDER_STATE_UNKNOWN" in service
    assert "ClientUpdateState.UpdateBlocked" in service
    assert "response.Health is null" in service
    assert "Never install while Recorder liveness is unknown" in service
    assert "STARTING" in service and "FINALIZING" in service
    assert "StartMonitoring()" in app
    assert "currentBuildIdentity" in service
    assert "UPDATE_PACKAGE_CHECKSUM_MISMATCH" in service
    assert "UPDATE_UNSIGNED_PILOT_CONFIRMATION_REQUIRED" in service
    assert "InstallAsync(bool allowUnsignedPilot" in service


def test_update_install_is_fail_closed_when_recorder_health_is_unknown():
    service = read("apps/desktop/WhisperX.Atom.Desktop/Services/ClientUpdateService.cs")
    assert "UpdateBlocked" in service
    assert 'SetState(ClientUpdateState.UpdateBlocked, "RECORDER_STATE_UNKNOWN")' in service


def test_desktop_surfaces_server_protocol_and_minimum_version_mismatch():
    service = read("apps/desktop/WhisperX.Atom.Desktop/Services/ClientUpdateService.cs")
    window = read("apps/desktop/WhisperX.Atom.Desktop/MainWindow.xaml.cs")
    assert "GetServerCompatibilityError" in service
    assert "API_VERSION_MISMATCH" in service
    assert "VERSION_MISMATCH" in service
    assert "GetServerCompatibilityError(serverVersion)" in window
    assert "Требуется обновление Desktop для этого сервера" in window
    assert "if (!response.IsReachable || response.Health is null)" in service
    assert "catch\n        {\n            // Never install while Recorder liveness is unknown." in service
    assert "IsKnownRecorderState" in service
    assert 'SetState(ClientUpdateState.UpdateBlocked, "RECORDER_STATE_UNKNOWN")' in service


def test_recording_e2e_script_covers_faults_without_implicit_destructive_actions():
    script = read("scripts/e2e-recording.ps1")
    for scenario in ("pause-resume", "server-offline", "api-crash", "workers-restart", "desktop-close", "usb-loss", "low-disk", "host-crash"):
        assert f'"{scenario}"' in script
    assert "ExecuteFaults" in script
    assert "RECORDER_E2E" in script
    assert "safety = [ordered]@{ audioIncluded = $false" in script
    assert "rawWritingCount" in script
    assert "E2E_HOST_ALREADY_RUNNING_FOR_ISOLATED_ROOT" in script


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
    assert "RemoveFilesNotInSnapshot" in updater
    assert "File.Delete(file)" in updater
    assert "Directory.Delete(directory, true)" in updater
    assert "WhisperX.Atom.Updater.exe" in publish
    # Desktop wildcard payload includes the helper without adding a second
    # installer source that could diverge from the published identity.
    assert 'Source: "..\\..\\..\\artifacts\\desktop\\Desktop\\*"' in iss


def test_installer_release_manifest_rejects_dirty_or_mixed_runtime():
    script = read("scripts/build-installer.ps1")
    assert "INSTALLER_RELEASE_IDENTITY_INVALID" in script
    assert "INSTALLER_RUNTIME_IDENTITY_MISMATCH" in script
    assert "identity.dirty" in script


def test_installer_forwards_tts_wheelhouse_by_named_parameter():
    script = read("scripts/build-installer.ps1")
    publisher = read("scripts/publish-desktop.ps1")
    # Positional splatting can bind the switch as publish-desktop.ps1's
    # OutputRoot on Windows PowerShell, silently preserving a stale Desktop.
    assert "& $publish -NoRestore -TtsWheelhouse $TtsWheelhouse" in script
    assert "& $publish -TtsWheelhouse $TtsWheelhouse" in script
    assert "& $publish @publishArgs" not in script
    assert "& $ttsPublisher -OutputRoot (Join-Path $output 'TtsHost') -AllowGeneratedStagingDirty -WheelhouseRoot $TtsWheelhouse" in publisher
    assert "& powershell.exe @ttsArgs" not in publisher


def test_clean_runtime_gate_requires_one_identity_and_excludes_legacy_python_payload():
    script = read("scripts/verify-clean-runtime.ps1")
    publish = read("scripts/publish-desktop.ps1")
    bundle = read("scripts/build-server-bundle.ps1")
    assert "RUNTIME_RELEASE_IDENTITY_INVALID" in script
    assert "RUNTIME_ARTIFACT_IDENTITY_MISMATCH" in script
    assert "RUNTIME_INSTALLED_IDENTITY_MISMATCH" in script
    assert "RUNTIME_SERVER_IDENTITY_MISMATCH" in script
    assert "RUNTIME_LEGACY_PYTHON_IN_PAYLOAD" in script
    assert "app.py" in script
    assert "productionPath" in script
    assert "AudioGraph Recorder Host" in script
    assert "--untracked-files=normal" in publish
    assert "GIT_STATUS_FAILED" in publish
    assert "2>NUL" in publish
    assert "PRODUCTION_PAYLOAD_CONTAINS_LEGACY_PYTHON" in publish
    assert "RELEASE_COMPONENT_IDENTITY_MISMATCH" in publish
    assert "--untracked-files=normal" in bundle
    assert "GIT_STATUS_FAILED" in bundle
    assert "2>NUL" in bundle
    assert "servicesToBuild" in bundle
    assert "RELEASE_IMAGES_ALREADY_BUILT" in bundle
    assert 'docker image inspect `"$image`" >NUL 2>NUL' in bundle
    assert "RELEASE_SERVER_ARCHIVE_CREATE_FAILED" in bundle
    assert "tar.exe -a -c -f $archive -C $finalRoot ." in bundle
    assert "$allImages.Contains" in bundle


def test_runtime_documentation_declares_single_supported_production_path():
    docs = read("docs/architecture-current.md")
    assert "Поддерживаемая конфигурация" in docs
    assert "AudioGraph Recorder Host" in docs or "AudioGraph" in docs
    assert "не\nобразуют второй поддерживаемый server pipeline" in docs
