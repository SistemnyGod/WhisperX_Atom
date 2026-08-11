from pathlib import Path


ROOT = Path(__file__).parents[1]
API = ROOT / "apps/server/WhisperX.Atom.Api/Program.cs"
STORE = ROOT / "apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs"
MIGRATION = ROOT / "apps/server/WhisperX.Atom.Api/Migrations/012_persistent_auth_and_agent_link.sql"
DESKTOP_API = ROOT / "apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs"
DESKTOP_SETTINGS = ROOT / "apps/desktop/WhisperX.Atom.Desktop/DesktopSettings.cs"
AGENT = ROOT / "apps/recorder-agent/AgentApiClient.cs"
IPC = ROOT / "apps/recorder-agent/AgentIpcProtocol.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def test_server_exposes_rotating_refresh_and_local_agent_linking():
    api = read(API)
    store = read(STORE)
    migration = read(MIGRATION)

    assert 'app.MapPost("/api/auth/refresh"' in api
    assert 'app.MapPost("/api/agents/link-local"' in api
    assert "RotateRefreshSessionAsync" in api
    assert "LinkLocalAgentAsync" in api
    assert "refresh_sessions" in migration
    assert "installation_id" in migration
    assert "installation_id" in store


def test_desktop_refreshes_once_and_persists_the_rotated_cookie():
    api = read(DESKTOP_API)
    settings = read(DESKTOP_SETTINGS)

    assert 'api/auth/refresh' in api
    assert "SemaphoreSlim _refreshGate" in api
    assert "SendAuthorizedAsync" in api
    assert "SessionChanged" in api
    assert "ProtectedData.Protect" in settings
    assert "SessionExpiresAtUtc" in settings


def test_agent_keeps_installation_identity_and_reports_server_state():
    agent = read(AGENT)
    ipc = read(IPC)

    assert "_installationId" in agent
    assert "InstallationId" in agent
    assert "ScheduleHeartbeatRetry" in agent
    assert "AUTH_REJECTED" in agent
    assert "InstallationId" in ipc
    assert "ServerConnectionState" in ipc
