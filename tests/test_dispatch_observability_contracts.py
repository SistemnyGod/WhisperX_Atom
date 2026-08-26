from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_jobs_expose_dispatch_observability_without_breaking_legacy_columns():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    desktop = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/046_job_dispatch_observability.sql")
    for field in ("not_before", "scheduled_reason", "queue_entered_at", "worker_claimed_at"):
        assert field in migration
    for field in ("NotBefore", "ScheduledReason", "QueueEnteredAt", "WorkerClaimedAt", "DispatchState"):
        assert field in api
        assert field in desktop
    for state in ("SCHEDULED", "WAITING_FOR_OUTBOX", "WAITING_FOR_GPU", "PROCESSING"):
        assert state in api


def test_supervisor_readiness_is_token_scoped_and_not_admin_scoped():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    supervisor = read("scripts/supervise-server-runtime.ps1")
    assert '"/api/internal/runtime/readiness"' in api
    assert "X-WhisperX-Supervisor-Token" in api
    assert "CryptographicOperations.FixedTimeEquals" in api
    assert "SUPERVISOR_HEALTH_TOKEN" in supervisor
    assert "/api/auth/login" not in supervisor


def test_supervisor_has_dynamic_services_targeted_recovery_and_budget():
    supervisor = read("scripts/supervise-server-runtime.ps1")
    assert 'if ((Read-EnvValue "AUTO_SUMMARY_ENABLED") -eq "true"' in supervisor
    assert "Targeted restart requested" in supervisor
    assert "Restart budget exhausted" in supervisor
    assert "recover-gpu-runtime.ps1" in supervisor
    assert "compose up -d --no-deps --pull never gpu-worker" in supervisor


def test_server_task_starts_current_session_immediately():
    startup = read("scripts/install-server-startup-task.ps1")
    assert "Start-ScheduledTask" in startup
    assert "-LogonType Interactive" in startup
    assert "MultipleInstances IgnoreNew" in startup
    assert "Get-TaskAccountComponent" in startup
    assert "-ErrorAction Stop" in startup
    assert '$registeredMultipleInstances = ([string]$registered.Settings.MultipleInstances).Trim()' in startup
    assert '$registeredMultipleInstances -ne "IgnoreNew"' in startup
