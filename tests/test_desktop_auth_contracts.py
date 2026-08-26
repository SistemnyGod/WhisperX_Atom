from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_lan_admin_password_setup_is_one_time_and_does_not_store_plaintext():
    script = read("scripts/set-lan-admin-password.ps1")
    assert "[PSCredential]$Credential" in script
    assert "RandomNumberGenerator]::Create()" in script
    assert "Rfc2898DeriveBytes" in script and "GetBytes(32)" in script
    assert "120000" in script
    assert "SHA256" in script
    assert "UPDATE users" in script
    assert "UPDATE sessions" in script and "UPDATE refresh_sessions" in script
    assert "dsdjl" + "123" not in script
    assert "BOOTSTRAP_ADMIN_PASSWORD" not in script


def test_login_window_and_app_share_one_frontend_session():
    app = read("apps/desktop/WhisperX.Atom.Desktop/App.xaml.cs")
    window = read("apps/desktop/WhisperX.Atom.Desktop/LoginWindow.xaml.cs")
    services = read("apps/desktop/WhisperX.Atom.Desktop/Services/FrontendContracts.cs")
    assert "FrontendServices(" in app
    assert "new LoginWindow(_services, OnAuthenticatedAsync, message)" in app
    assert "Backend.LoginAsync" in window
    assert "AgentBootstrap.EnsureAgentReadyAsync" in window
    assert "public AgentBootstrapCoordinator AgentBootstrap" in services
    assert "event Action? LoggedOut" in services


def test_offline_mode_requires_protected_session_owner_and_confirmed_bootstrap():
    backend = read("apps/desktop/WhisperX.Atom.Desktop/Services/BackendService.cs")
    settings = read("apps/desktop/WhisperX.Atom.Desktop/DesktopSettings.cs")
    coordinator = read("apps/desktop/WhisperX.Atom.Desktop/Services/AgentBootstrapCoordinator.cs")
    assert "ProtectedSessionCookie" in backend
    assert "OwnerUserId" in backend
    assert "AgentBootstrapConfirmed" in backend
    assert "AgentBootstrapConfirmed = false" in settings
    assert "AgentBootstrapConfirmed = true" in coordinator
    assert "REENROLL_REQUIRED" in coordinator


def test_settings_no_longer_exposes_initial_login_form():
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/SettingsPage.xaml")
    login_window = read("apps/desktop/WhisperX.Atom.Desktop/LoginWindow.xaml")
    assert 'x:Name="LoginButton"' not in page
    assert 'x:Name="PasswordBox"' in login_window
    assert 'Click="LoginButton_Click"' in login_window


def test_recorder_and_server_failures_are_reported_independently():
    coordinator = read("apps/desktop/WhisperX.Atom.Desktop/Services/AgentBootstrapCoordinator.cs")
    main_window = read("apps/desktop/WhisperX.Atom.Desktop/MainWindow.xaml.cs")
    assert "RECORDER_IPC_TIMEOUT" in coordinator
    assert "Named Pipe недоступен" in coordinator
    assert "Task.WhenAll(backendTask, versionTask, recorderTask)" not in main_window
    assert "LAN-сервер доступен; Recorder Service не запущен" in main_window
