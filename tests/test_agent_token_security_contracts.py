from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_production_tls_redirect_does_not_disable_auto_https():
    caddy = read("infrastructure/caddy/Caddyfile.prod")
    assert "auto_https off" not in caddy
    assert "redir https://{$PUBLIC_HOST}{uri} permanent" in caddy
    assert "tls /etc/caddy/certs/fullchain.pem /etc/caddy/certs/privkey.pem" in caddy


def test_agent_rotate_and_revoke_are_admin_only_and_hash_only():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/020_agent_token_lifecycle.sql")
    for endpoint in ("rotate-token", "/revoke", "/reenroll"):
        assert endpoint in api
    assert api.count("IsAdministrator(context)") >= 3
    assert "token_revoked_at IS NULL" in store
    assert "enrollment_hash=@hash" in store
    assert "token_revoked_at" in migration
    assert 'error = "AUTH_REJECTED"' in api


def test_agent_token_is_never_logged_and_desktop_handles_rejection():
    server = read("apps/server/WhisperX.Atom.Api/Program.cs")
    agent = read("apps/recorder-agent/AgentApiClient.cs")
    desktop = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/RecordingViewModel.cs")
    assert "Logger.Log" not in server.split('app.MapPost("/api/agents/{agentId:guid}/rotate-token"', 1)[1].split('app.MapPost("/api/agents/link-local"', 1)[0]
    assert "ProtectedData.Protect" in agent
    assert "AGENT_AUTH_REJECTED" in desktop


def test_regular_meeting_routes_use_owner_access_guard():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    # Read-only canonical meeting routes may use the configured deployment
    # scope; mutations must continue to use the owner/admin guard.
    assert api.count("CanAccessMeetingAsync(context, id)") + api.count("CanReadMeetingAsync(context, id)") >= 14
    assert api.count("CanAccessMeetingAsync(context, id)") >= 5
    assert "owner_id=@owner" in api
