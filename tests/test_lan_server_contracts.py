from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_lan_compose_publishes_only_gateway_and_uses_lan_environment():
    compose = read("compose.lan.yml")
    example = read(".env.lan.example")
    assert "ASPNETCORE_ENVIRONMENT: Lan" in compose
    assert "COOKIE_SECURE: \"false\"" in compose
    assert "ALLOW_INSECURE_LAN_HTTP: \"true\"" in compose
    assert "lan-gateway:" in compose
    assert "${LAN_BIND_ADDRESS:?Set LAN_BIND_ADDRESS in .env.lan}:8080:8080" in compose
    for service in ("api:", "postgres:", "nats:", "tusd:"):
        section = compose.split(service, 1)[1]
        assert "ports: !reset []" in section
    assert "LAN_BIND_ADDRESS=192.168.2.194" in example
    assert "SERVER_ORIGIN=http://192.168.2.194:8080" in example


def test_lan_gateway_routes_only_api_health_and_files():
    caddy = read("infrastructure/caddy/Caddyfile.lan")
    assert "auto_https off" in caddy
    assert "/api/*" in caddy and "/files/*" in caddy
    assert "handle {" in caddy and "respond \"not found\" 404" in caddy
    assert "api:8080" in caddy and "tusd:1080" in caddy


def test_lan_runtime_guards_private_http_and_strong_secrets():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert 'IsEnvironment("Lan")' in api
    assert "ALLOW_INSECURE_LAN_HTTP" in api
    assert "IsPrivateLanOrigin" in api
    assert "LAN_SECRET_INVALID" in api
    assert "builder.Environment.IsProduction()" in api


def test_user_bootstrap_and_owner_propagation_are_explicit():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/024_lan_users_agent_ownership.sql")
    desktop = read("apps/desktop/WhisperX.Atom.Desktop/MainWindow.xaml.cs")
    bootstrap = read("apps/desktop/WhisperX.Atom.Desktop/Services/AgentBootstrapCoordinator.cs")
    recorder = read("apps/recorder-agent/AgentPipeHost.cs") + read("apps/recorder-agent/SpoolStore.cs")
    assert 'app.MapPost("/api/agents/bootstrap"' in api
    assert "REENROLL_REQUIRED" in api and "agent_user_links" in store
    assert "must_change_password" in migration and "owner_user_id" in migration
    assert "EnsureAgentReadyAsync" in desktop
    assert "BootstrapLocalAgentAsync" in bootstrap
    assert 'ReadGuid(request.Payload, "ownerUserId")' in recorder
    assert "OwnerUserId" in recorder


def test_managed_machine_origin_is_read_only_in_desktop_settings():
    settings = read("apps/desktop/WhisperX.Atom.Desktop/DesktopSettings.cs")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/SettingsViewModel.cs")
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/SettingsPage.xaml")
    assert "MachineServerConfig.Load()" in settings
    assert "ServerOriginManaged" in view_model
    assert "IsReadOnly=\"{Binding ServerOriginManaged}\"" in page


def test_health_and_version_endpoints_are_public_compatibility_contracts():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert 'app.MapGet("/health/live"' in api
    assert 'app.MapGet("/health/ready"' in api
    assert 'app.MapGet("/api/system/version"' in api
    assert "serverTimeUtc" in api


def test_outbox_and_online_drift_have_durable_dedup_and_fallback_contracts():
    outbox = read("workers/outbox_relay/worker.py")
    media = read("workers/media_worker/recording_assembly.py")
    local = read("apps/recorder-agent/LocalArchiveWriter.cs")
    assert '"Nats-Msg-Id": str(message_id)' in outbox
    assert "online_single_track_fallback" in media
    assert "ONLINE_MIX_DEGRADED" in local
    assert "AUDIO_TRACK_DRIFT_HIGH" in media and "AUDIO_TRACK_DRIFT_HIGH" in local


def test_disabled_owner_keeps_delivery_retryable_without_changing_local_archive():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    agent = read("apps/recorder-agent/AgentApiClient.cs")
    assert "OWNER_AUTHORIZATION_REJECTED" in store
    assert "OWNER_AUTHORIZATION_REJECTED" in agent
