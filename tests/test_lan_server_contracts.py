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


def test_client_installer_pins_the_managed_lan_server_origin():
    agent = read("apps/recorder-agent/AgentApiClient.cs")
    desktop = read("apps/desktop/WhisperX.Atom.Desktop/DesktopSettings.cs")
    installer = read("apps/desktop/Installer/Install-Service.ps1")
    iss = read("apps/desktop/Installer/WhisperXAtom.iss")
    assert "UnconfiguredServerSink" in agent
    assert "127.0.0.1:0" in desktop
    assert 'ServerOrigin = "http://192.168.2.194:8080"' in installer
    assert 'defaultServerOrigin = "http://192.168.2.194:8080"' in installer
    assert '-ServerOrigin ""{#ServerOrigin}""' in iss
    assert "192.168.2.194" not in desktop
    assert "uri.IsLoopback && uri.Port == 0" in agent
    builder = read("scripts/build-installer.ps1")
    assert "[string]$ServerOrigin" in builder
    assert "/DServerOrigin=" in builder


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


def test_system_readiness_separates_gateway_recording_ingress_and_whisperx():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    readiness = api.split('app.MapGet("/api/system/readiness"', 1)[1].split('app.MapGet("/api/system/status"', 1)[0]
    for component in ("gateway", "recordingIngress", "whisperx", "storage"):
        assert component in readiness


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


def test_desktop_recovers_missing_local_agent_identity_without_rotating_healthy_tokens():
    bootstrap = read("apps/desktop/WhisperX.Atom.Desktop/Services/AgentBootstrapCoordinator.cs")
    recovery = bootstrap.split("var enrollmentToken = enrollment.Token;", 1)[1].split(
        "if (!string.IsNullOrWhiteSpace(enrollmentToken))", 1
    )[0]
    assert "agentHealth.AgentId is null" in recovery
    assert "ReenrollAgentAsync(agentId" in recovery
    assert "AGENT_RECOVERY_REQUIRES_ADMIN" in recovery
    assert "enrollment.ReenrollRequired" in bootstrap
    assert bootstrap.index("enrollment.ReenrollRequired") < bootstrap.index("ReenrollAgentAsync(agentId")


def test_bootstrap_null_agent_is_typed_and_api_errors_are_json():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")

    assert "using NpgsqlTypes;" in store
    assert 'find.Parameters.Add("agent", NpgsqlDbType.Uuid)' in store
    assert "INVALID_LOGIN_REQUEST" in api
    assert 'error = "INTERNAL_SERVER_ERROR"' in api
    assert "traceId" in api
    bootstrap = client.split("public async Task<DesktopAgentBootstrapResult> BootstrapLocalAgentAsync", 1)[1].split("public async Task<DesktopAgentEnrollment> ReenrollAgentAsync", 1)[0]
    assert "ReadAsStringAsync(cancellationToken)" in bootstrap
    assert "AGENT_BOOTSTRAP_RESPONSE_EMPTY" in bootstrap
    assert "AGENT_BOOTSTRAP_RESPONSE_INVALID" in bootstrap


def test_managed_machine_origin_is_read_only_in_desktop_settings():
    settings = read("apps/desktop/WhisperX.Atom.Desktop/DesktopSettings.cs")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/SettingsViewModel.cs")
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/SettingsPage.xaml")
    assert "MachineServerConfig.Load()" in settings
    assert "ServerOriginManaged" in view_model
    assert "IsReadOnly=\"{Binding ServerOriginManaged}\"" in page


def test_unmanaged_server_origin_can_be_applied_and_revalidated_from_settings():
    settings = read("apps/desktop/WhisperX.Atom.Desktop/DesktopSettings.cs")
    view_model = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/SettingsViewModel.cs")
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/SettingsPage.xaml")
    assert "ApplyServerOriginAsync" in view_model
    assert "Uri.UriSchemeHttp" in view_model and "Uri.UriSchemeHttps" in view_model
    assert "_services.Backend.ApplySettings(updated)" in view_model
    assert "ApplyServerOriginButton_Click" in page
    assert "!IsHttpUrl(configuredUrl)" in settings
    assert "IsUnconfiguredUrl(configuredUrl)" in settings
    assert "LastConnectionErrorCode" in read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    assert "SERVER_NETWORK_UNREACHABLE" in view_model and "SERVER_TIMEOUT" in view_model
    assert "uri.Port == 0" in read("apps/desktop/WhisperX.Atom.Desktop/MachineServerConfig.cs")


def test_recorder_agent_accepts_explicit_local_http_origin_and_rejects_non_http_urls():
    agent = read("apps/recorder-agent/AgentApiClient.cs")
    assert "if (!IsHttpUrl(serverUrl)" in agent
    assert "if (!HasCredentials || !IsHttpUrl(serverUrl)" in agent
    assert "uri.IsLoopback && uri.Port == 0" in agent


def test_health_and_version_endpoints_are_public_compatibility_contracts():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert 'app.MapGet("/health/live"' in api
    assert 'app.MapGet("/health/ready"' in api
    assert 'app.MapGet("/api/system/version"' in api
    assert "serverTimeUtc" in api


def test_installed_desktop_uses_direct_lan_http_without_inheriting_proxy_settings():
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    assert "UseProxy = false" in client
    assert "External proxy support is not part of" in client


def test_readiness_checks_are_concurrent_safe_and_degrade_to_structured_status():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert 'AddHttpClient("nats-readiness"' in api
    assert 'CreateClient("nats-readiness")' in api
    assert '$".ready-probe-{Guid.NewGuid():N}"' in api
    assert 'System readiness worker or operations query failed' in api
    assert 'IReadOnlyList<WorkerRuntimeRow> workers = Array.Empty<WorkerRuntimeRow>()' in api


def test_system_readiness_does_not_report_failed_workers_as_ready_cuda():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert 'var gpuWorkerFailed = gpuWorker is null' in api
    assert 'var gpuStatus = !cuda || gpuWorkerFailed' in api
    assert "IsActiveWorker(worker)" in api


def test_system_readiness_does_not_promote_starting_workers_to_ready():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert "static bool IsActiveWorker" in api
    assert 'string.Equals(worker.Status, "READY"' in api
    assert 'string.Equals(worker.Status, "BUSY"' in api
    assert 'string.Equals(worker.Status, "DEGRADED"' in api
    assert 'summary_worker_starting' in api
    assert "static bool IsFreshWorker" in api
    assert "age >= TimeSpan.Zero" in api


def test_legacy_lan_launcher_does_not_mutate_pipeline_recovery_state():
    launcher = read("scripts/start-whisperx-lan-server.ps1")
    assert "OperationalRecoveryService" in launcher
    assert "STARTUP_RECOVERY_FAILED" not in launcher
    assert "UPDATE jobs" not in launcher
    assert "UPDATE recording_sessions" not in launcher


def test_async_worker_heartbeat_overwrites_stale_ready_state_on_restart():
    heartbeat = read("workers/runtime_heartbeat.py")
    assert "Publish STARTING before any NATS/model setup" in heartbeat
    assert "await asyncio.to_thread(self._write)" in heartbeat
    assert "heartbeat_write_failed worker=%s error=%s" in heartbeat


def test_outbox_and_online_drift_have_durable_dedup_and_fallback_contracts():
    outbox = read("workers/outbox_relay/worker.py")
    media = read("workers/media_worker/recording_assembly.py")
    local = read("apps/recorder-agent/LocalArchiveWriter.cs")
    assert '"Nats-Msg-Id": str(message_id)' in outbox
    assert "online_single_track_fallback" in media
    assert "ONLINE_MIX_DEGRADED" in local
    assert "AUDIO_TRACK_DRIFT_HIGH" in media and "AUDIO_TRACK_DRIFT_HIGH" in local


def test_outbox_relay_does_not_hold_postgres_work_during_nats_publish():
    outbox = read("workers/outbox_relay/worker.py")
    migration = read("apps/server/WhisperX.Atom.Api/Migrations/026_outbox_pending_index.sql")
    publish_start = outbox.index("await jetstream.publish")
    update_connection_start = outbox.index("with psycopg.connect(conninfo) as connection:", publish_start)
    publish = outbox[publish_start:update_connection_start]
    assert "with psycopg.connect(conninfo) as connection:" in outbox
    assert "with psycopg.connect(conninfo) as connection:" not in publish
    assert "published_at IS NULL" in migration
    assert "WHERE published_at IS NULL" in migration


def test_disabled_owner_keeps_delivery_retryable_without_changing_local_archive():
    store = read("apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs")
    agent = read("apps/recorder-agent/AgentApiClient.cs")
    assert "OWNER_AUTHORIZATION_REJECTED" in store
    assert "OWNER_AUTHORIZATION_REJECTED" in agent


def test_gpu_readiness_distinguishes_busy_from_unavailable():
    program = read("apps/server/WhisperX.Atom.Api/Program.cs")
    assert 'gpuStatus = !cuda' in program
    assert '"BUSY"' in program
    compose = read("compose.dev.yml")
    healthcheck = read("workers/ml_worker/healthcheck.py")
    assert "GPU_WORKER_MODE" in compose
    assert "capabilities.get(\"cudaAvailable\")" in healthcheck
    assert "torch.cuda.is_available" not in healthcheck
    assert '"STARTING"' in healthcheck
    assert "0 <= age <= 60" in healthcheck


def test_worker_container_healthchecks_validate_live_heartbeats_not_python_imports():
    compose = read("compose.dev.yml")
    healthcheck = read("workers/runtime_healthcheck.py")
    assert "workers.runtime_healthcheck" in compose
    assert "import workers." not in compose
    assert "worker_instances" in healthcheck
    assert "last_seen_at" in healthcheck
    assert "NON_READY_STATES" in healthcheck
    assert '"STARTING"' in healthcheck


def test_workers_fail_closed_when_jetstream_stream_setup_fails():
    helper = read("workers/nats_utils.py")
    assert "async def ensure_stream" in helper
    assert "nats_stream_unavailable" in helper
    for worker in (
        "workers/outbox_relay/worker.py",
        "workers/media_worker/worker.py",
        "workers/ml_worker/worker.py",
        "workers/summary_worker/worker.py",
    ):
        source = read(worker)
        assert "ensure_stream(" in source
        assert "except Exception:\n        pass" not in source


def test_compose_assigns_stable_worker_instance_ids_for_healthchecks():
    compose = read("compose.dev.yml")
    for worker in ("outbox-relay", "import-worker", "media-worker", "gpu-worker", "summary-worker"):
        assert f"WORKER_INSTANCE_ID: {worker}" in compose or (
            worker == "gpu-worker" and "WORKER_INSTANCE_ID: ${GPU_WORKER_INSTANCE_ID:-gpu-worker}" in compose
        )


def test_worker_message_failures_are_logged_before_redelivery():
    assert 'LOGGER.exception("gpu_message_failed job_id=%s", job_id)' in read("workers/ml_worker/worker.py")
    summary = read("workers/summary_worker/worker.py")
    assert 'LOGGER.exception("summary_message_failed job_id=%s", job_id)' in summary
    assert 'LOGGER.error("summary job=%s entered terminal failure: %s", job_id, detail)' in summary
    terminal = summary.split("self.repository.mark_failed(job_id, meeting_id, detail, message_id=message_id)", 1)[1]
    assert "return" in terminal.split("async def run", 1)[0]
    assert 'LOGGER.exception("assistant_message_failed query_id=%s", query_id)' in summary
    assert "gpu_poison_message_discarded" in read("workers/ml_worker/worker.py")
    assert "media_poison_message_discarded" in read("workers/media_worker/worker.py")
    assert "resolve_media_path" in read("workers/media_worker/worker.py")
    assert "summary_poison_message_discarded" in summary
    assert "assistant_poison_message_discarded" in summary


def test_lan_launcher_waits_for_worker_health_not_only_running_state():
    launcher = read("scripts/start-whisperx-lan-server.ps1")
    assert "Test-WorkerHealthy" in launcher
    assert "LAN_WORKER_HEALTH_FAILED" in launcher
    assert "State.Health" in launcher
    assert 'return $health -eq "healthy"' in launcher
    assert '"no-healthcheck"' not in launcher
    doctor = read("scripts/doctor-whisperx-lan-server.ps1")
    assert "PROCESSING_SERVICES_UNHEALTHY" in doctor
    assert "{{.Service}} {{.State}} {{.Health}}" in doctor
    assert 'if ((Read-EnvValue "AUTO_SUMMARY_ENABLED") -eq "true") { $required += "summary-worker" }' in doctor


def test_lan_launcher_persists_qwen_and_assistant_flags_for_startup():
    launcher = read("scripts/start-whisperx-lan-server.ps1")
    startup = read("scripts/install-whisperx-lan-startup-task.ps1")
    assert 'if ($autoSummary -eq "true") { $EnableQwen = $true }' in launcher
    assert 'assistant = if ($assistantEnabled -eq "true" -or $EnableAssistant) { "ENABLED" }' in launcher
    assert "-EnableQwen" in startup and "-EnableAssistant" in startup


def test_server_runtime_supervisor_is_user_session_owned_and_bounded():
    supervisor = read("scripts/supervise-server-runtime.ps1")
    startup = read("scripts/install-server-startup-task.ps1")
    bundle = read("scripts/build-server-bundle.ps1")
    assert "Global\\WhisperXAtom.ServerSupervisor" in supervisor
    assert "DOCKER_DESKTOP_NOT_FOUND" in supervisor
    assert "Start-DockerDesktopIfNeeded" in supervisor
    assert "DockerTimeoutSeconds = 600" in supervisor
    assert "SUPERVISOR_HEALTH_TOKEN" in supervisor
    assert "/api/internal/runtime/readiness" in supervisor
    assert "Targeted restart requested" in supervisor
    assert "Restart budget exhausted" in supervisor
    assert "orphanedGpuJobs" in supervisor
    assert "releaseIdentityValid" in supervisor
    assert "summary-worker" in supervisor
    assert "RestartCount 20" in startup and "RestartInterval" in startup
    assert "AtLogOn" in startup and "-LogonType Interactive" in startup
    assert "Start-ScheduledTask" in startup
    assert "release-manifest.json" in startup and "DOCKER_DESKTOP_NOT_FOUND" in startup
    assert "supervise-server-runtime.ps1" in bundle
    assert "ensure-supervisor-health-token.ps1" in bundle
    assert "runtimeScripts" in bundle


def test_reconnect_makes_only_retryable_delivery_due():
    spool = read("apps/recorder-agent/SpoolStore.cs")
    client = read("apps/recorder-agent/AgentApiClient.cs")
    assert "MakeRetryableDeliveriesDueAsync" in spool
    assert "last_error_retryable=1" in spool
    assert "AGENT_AUTH_REJECTED" in spool and "MEETING_OWNER_MISMATCH" in spool
    assert "_spool.MakeRetryableDeliveriesDueAsync" in client


def test_summary_worker_image_contains_shared_runtime_modules():
    dockerfile = read("workers/summary_worker/Dockerfile")
    assert "COPY workers/db_pool.py /srv/workers/db_pool.py" in dockerfile
    assert "COPY workers/runtime_heartbeat.py /srv/workers/runtime_heartbeat.py" in dockerfile


def test_installer_requires_pinned_ffmpeg_payload_and_manifest():
    installer = read("apps/desktop/Installer/Install-Service.ps1")
    publish = read("scripts/publish-desktop.ps1")
    staging = read("scripts/stage-ffmpeg-payload.ps1")
    assert 'Join-Path $ServiceDirectory "ffmpeg.exe"' in installer
    assert "ffmpeg-manifest.json" in publish
    assert "Get-FileHash" in publish
    assert "sha256" in staging
    assert "Remove-Item \"Env:$proxyVariable\"" in publish
    assert "MSBuildEnableWorkloadResolver" in publish


def test_firewall_allows_private_lan_and_blocks_public_profile():
    firewall = read("scripts/configure-whisperx-lan-firewall.ps1")
    assert "Profile Domain,Private" in firewall
    assert "Profile Public" in firewall
    assert '"$RuleName (Public block)"' in firewall
