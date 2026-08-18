from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DESKTOP = ROOT / "apps" / "desktop" / "WhisperX.Atom.Desktop"


def test_phase_one_shell_prioritizes_recording_and_hides_secondary_routes():
    shell = (DESKTOP / "MainWindow.xaml").read_text(encoding="utf-8")
    assert 'Text="Главная"' in shell
    assert 'Text="Recorder · проверка"' in shell
    assert 'Visibility="Collapsed" Content="Серии оперативок"' in shell
    assert 'Visibility="Collapsed" Content="Поиск"' in shell
    assert 'Text="WhisperX Atom Desktop"' not in shell


def test_recording_primary_actions_and_local_first_copy_are_visible():
    recording = (DESKTOP / "Pages" / "RecordingPage.xaml").read_text(encoding="utf-8")
    assert 'Content="Начать запись"' in recording
    assert 'Content="Завершить"' in recording
    assert 'Text="Локальное сохранение"' in recording
    assert 'Text="Системный звук"' in recording


def test_recording_controls_explain_local_first_flow_and_adapt_to_compact_widths():
    recording = (DESKTOP / "Pages" / "RecordingPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "RecordingPage.xaml.cs").read_text(encoding="utf-8")
    assert 'Text="Управление записью"' in recording
    assert 'ToolTipService.ToolTip="Запустить локальную запись"' in recording
    assert 'Text="ПОЗЖЕ"' in recording
    assert 'e.NewSize.Width < 760 ? Orientation.Vertical : Orientation.Horizontal' in codebehind
    assert 'локальную запись' in codebehind


def test_device_selector_exposes_accessible_name_without_refresh_selection_side_effects():
    selector = (DESKTOP / "Controls" / "DeviceSelector.xaml").read_text(encoding="utf-8")
    assert 'AutomationProperties.Name="{x:Bind Label, Mode=OneWay}"' in selector
    assert 'ToolTipService.ToolTip="{x:Bind HelperText, Mode=OneWay}"' in selector


def test_transcript_error_state_offers_connection_recovery_and_distinguishes_server_failure():
    page = (DESKTOP / "Pages" / "TranscriptsPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "TranscriptsPage.xaml.cs").read_text(encoding="utf-8")
    formatter = (DESKTOP / "Services" / "UiStatusMapper.cs").read_text(encoding="utf-8")
    shell = (DESKTOP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    assert 'Content="Настройки подключения"' in page
    assert 'NavigateTo("settings")' in codebehind
    assert "StatusCode.Value >= 500" in formatter
    assert '"Сервер · требуется вход"' in shell


def test_sources_screen_surfaces_effective_endpoint_signal_and_recovery_actions():
    page = (DESKTOP / "Pages" / "SourcesPage.xaml").read_text(encoding="utf-8")
    view_model = (DESKTOP / "ViewModels" / "SourcesViewModel.cs").read_text(encoding="utf-8")
    assert 'Content="Открыть настройки"' in page
    assert 'Content="Повторить проверку"' in page
    assert 'Text="{Binding EffectiveMicrophoneLabel}"' in page
    assert 'Text="{Binding MicrophoneSignal}"' in page
    assert 'AUDIO_SYSTEM_AUDIO_DEFERRED' in view_model
    assert '"Сохранённое устройство недоступно"' in view_model


def test_home_dashboard_does_not_advertise_recording_when_recorder_is_unavailable():
    page = (DESKTOP / "Pages" / "HomePage.xaml").read_text(encoding="utf-8")
    view_model = (DESKTOP / "ViewModels" / "HomeViewModel.cs").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "HomePage.xaml.cs").read_text(encoding="utf-8")
    assert 'Text="{Binding EffectiveMicrophoneText}"' in page
    assert 'Text="{Binding MicrophoneSignalText}"' in page
    assert '"Unavailable" => "Запись недоступна"' in view_model
    assert 'MicrophoneSignalState is "READY_NO_SIGNAL" or "CLIPPING"' in codebehind


def test_transcripts_distinguish_empty_registry_from_empty_search_and_keep_selection_visible_while_loading():
    page = (DESKTOP / "Pages" / "TranscriptsPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "TranscriptsPage.xaml.cs").read_text(encoding="utf-8")
    view_model = (DESKTOP / "ViewModels" / "TranscriptsViewModel.cs").read_text(encoding="utf-8")
    assert 'Content="Сбросить поиск"' in page
    assert '"Ничего не найдено"' in codebehind
    assert 'По запросу «{_viewModel.SearchText.Trim()}» совпадений нет.' in codebehind
    assert 'public bool HasSearchNoResults' in view_model
    assert '_viewModel.SelectedItem = item;' in codebehind
    assert 'CancellationTokenSource? _detailCts' in codebehind
    assert 'CancelDetailLoad();' in codebehind


def test_meetings_filter_state_explains_zero_results_and_offers_one_click_reset():
    page = (DESKTOP / "Pages" / "MeetingsPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "MeetingsPage.xaml.cs").read_text(encoding="utf-8")
    assert 'Content="Сбросить фильтры"' in page
    assert 'ClearFiltersButton.Visibility' in codebehind
    assert 'Измените запрос или сбросьте фильтры' in codebehind
    assert 'StatusFilterCombo.SelectedItem = "Все статусы"' in codebehind


def test_meeting_pipeline_uses_human_statuses_for_queue_and_audio_failures():
    mapper = (DESKTOP / "Services" / "UiStatusMapper.cs").read_text(encoding="utf-8")
    view_model = (DESKTOP / "ViewModels" / "MeetingsViewModel.cs").read_text(encoding="utf-8")
    assert '["QUEUED"]' in mapper
    assert '["NO_SPEECH_DETECTED"]' in mapper
    assert '["MEDIA_INVALID"]' in mapper
    assert 'UiStatusMapper.Text(status)' in view_model


def test_settings_keeps_connection_actions_after_authentication():
    page = (DESKTOP / "Pages" / "SettingsPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "SettingsPage.xaml.cs").read_text(encoding="utf-8")
    view_model = (DESKTOP / "ViewModels" / "SettingsViewModel.cs").read_text(encoding="utf-8")
    assert 'x:Name="LoginHintText"' in page
    assert 'x:Name="PasswordFieldsPanel"' in page
    assert 'x:Name="LoginButton"' not in page
    assert 'IsEnabled="{Binding CanReconnectAgent}"' in page
    assert 'IsEnabled="{Binding CanUseActions}"' in page
    assert 'CheckBackendAsync' in codebehind
    assert 'public bool CanUseActions' in view_model
    assert 'Directory.CreateDirectory(fullPath)' in view_model


def test_statuses_are_localized_without_replacing_server_codes():
    mapper = (DESKTOP / "Services" / "UiStatusMapper.cs").read_text(encoding="utf-8")
    agent = (DESKTOP / "ServerApiClient.cs").read_text(encoding="utf-8")
    tasks = (DESKTOP / "Pages" / "TasksPage.xaml").read_text(encoding="utf-8")
    agents = (DESKTOP / "Pages" / "AgentsPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "AgentsPage.xaml.cs").read_text(encoding="utf-8")
    assert '["NEEDS_REVIEW"]' in mapper
    assert '["REVOKED"]' in mapper
    assert 'EffectiveStatusText => UiStatusMapper.Text(EffectiveStatus)' in agent
    assert 'SelectedValuePath="Tag"' in tasks
    assert 'Content="На проверке" Tag="NEEDS_REVIEW"' in tasks
    assert 'Text="Недоступны:"' in agents
    assert 'HasAgents || _viewModel.IsLoading' in codebehind


def test_authenticated_user_can_reach_settings_when_recorder_is_down():
    login = (DESKTOP / "LoginWindow.xaml.cs").read_text(encoding="utf-8")
    app = (DESKTOP / "App.xaml.cs").read_text(encoding="utf-8")
    assert 'await _authenticated(bootstrap);' in login
    assert 'Do not trap an authenticated user in the login window.' in app
    assert 'ShowMainWindow();' in app


def test_login_window_surfaces_connection_context_and_keyboard_submit():
    page = (DESKTOP / "LoginWindow.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "LoginWindow.xaml.cs").read_text(encoding="utf-8")
    assert 'Text="Локальная запись"' in page
    assert 'x:Name="StatusSurface"' in page
    assert 'KeyDown="CredentialBox_KeyDown"' in page
    assert 'ConfigureInitialWindow();' in codebehind
    assert 'DisplayArea.GetFromWindowId' in codebehind
    assert 'width < 820' in codebehind
    assert 'Проверяем подключение и авторизацию' in codebehind
    assert 'StatusSurface.Opacity = 1' in codebehind
    assert 'RECORDER_BOOTSTRAP_FAILED' in codebehind
    assert 'Не удалось выполнить вход. Проверьте адрес сервера и локальное подключение.' in codebehind


def test_login_window_keeps_status_geometry_stable_and_reflows_statuses_on_compact_widths():
    page = (DESKTOP / "LoginWindow.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "LoginWindow.xaml.cs").read_text(encoding="utf-8")
    assert 'x:Name="LoginStatusGrid"' in page
    assert 'MinHeight="52" Opacity="0"' in page
    assert 'x:Name="BusyRing"' in page and 'Visibility="Collapsed"' in page
    assert 'private void ConfigureResponsiveLayout' in codebehind
    assert 'SetBusy(bool busy)' in codebehind


def test_voice_diagnostics_and_live_telemetry_have_separate_ui_ownership():
    vm = (DESKTOP / "ViewModels" / "SettingsViewModel.cs").read_text(encoding="utf-8")
    states = (DESKTOP / "ViewModels" / "VoiceUiStates.cs").read_text(encoding="utf-8")
    page = (DESKTOP / "Pages" / "SettingsPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "SettingsPage.xaml.cs").read_text(encoding="utf-8")
    assert 'VoiceDiagnosticsUiState' in states
    assert 'VoiceTelemetryUiState' in states
    assert 'ApplyVoiceDiagnostics' in vm
    assert 'VoiceRequestedMicrophone = packet.DeviceId' not in vm
    assert 'Task.Delay(TimeSpan.FromSeconds(2)' in codebehind
    assert 'UpdateVoiceStatusVisual();' not in codebehind.split('SubscribeTelemetryAsync', 1)[1].split('return Task.CompletedTask', 1)[0]
    assert 'Header="Техническая диагностика Мифодия" IsExpanded="False"' in page


def test_title_bar_uses_compact_status_pills_for_runtime_states():
    shell = (DESKTOP / "MainWindow.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    assert 'x:Name="RecorderStatusPill"' in shell
    assert 'x:Name="ServerStatusPill"' in shell
    assert 'x:Name="VoiceStatusPill"' in shell
    assert 'SetStatusPill(RecorderStatusPill' in codebehind
    assert 'SetStatusPill(VoiceStatusPill' in codebehind


def test_primary_ui_copy_avoids_backend_and_pipeline_jargon():
    settings = (DESKTOP / "Pages" / "SettingsPage.xaml").read_text(encoding="utf-8")
    agents = (DESKTOP / "Pages" / "AgentsPage.xaml").read_text(encoding="utf-8")
    meetings = (DESKTOP / "Pages" / "MeetingsPage.xaml").read_text(encoding="utf-8")
    home_vm = (DESKTOP / "ViewModels" / "HomeViewModel.cs").read_text(encoding="utf-8")
    assert 'Content="Проверить API"' in settings
    assert 'Подключения для текущей сессии API' in agents
    assert 'Text="Этап обработки"' in meetings
    assert '"ALIGNING" => "Выравнивание"' in home_vm


def test_sources_show_human_device_and_agent_states():
    contracts = (DESKTOP / "Services" / "FrontendContracts.cs").read_text(encoding="utf-8")
    page = (DESKTOP / "Pages" / "SourcesPage.xaml").read_text(encoding="utf-8")
    speakers = (DESKTOP / "Pages" / "SpeakersPage.xaml").read_text(encoding="utf-8")
    assert 'public string StateText' in contracts
    assert 'Text="{Binding StateText}"' in page
    assert 'Text="{Binding EffectiveStatusText}"' in page
    assert 'Зарегистрированные подключения текущей API-сессии' in page
    assert 'По ключу профиля из API' in speakers


def test_page_header_stacks_actions_on_compact_widths():
    header = (DESKTOP / "Controls" / "PageHeader.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Controls" / "PageHeader.xaml.cs").read_text(encoding="utf-8")
    assert 'SizeChanged="RootGrid_SizeChanged"' in header
    assert 'width < 680' in codebehind
    assert 'actions.Orientation = Orientation.Vertical' in codebehind
    assert 'Grid.SetRow(ActionsPresenter, 1)' in codebehind


def test_settings_changes_password_context_after_authentication():
    page = (DESKTOP / "Pages" / "SettingsPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "SettingsPage.xaml.cs").read_text(encoding="utf-8")
    assert 'LoginHintText.Visibility = ViewModel.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible' in codebehind
    assert 'PasswordFieldsPanel.Visibility = ViewModel.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed' in codebehind
    assert 'ChangePasswordButton.Visibility = ViewModel.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed' in codebehind


def test_settings_exposes_repeatable_setup_wizard_and_collapses_technical_details():
    page = (DESKTOP / "Pages" / "SettingsPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "SettingsPage.xaml.cs").read_text(encoding="utf-8")
    assert 'x:Name="OpenSetupWizardButton"' in page
    assert 'Content="Повторить настройку"' in page
    assert 'Header="Техническая диагностика Recorder Host"' in page
    assert 'Header="Мифодий"' in page
    assert 'OpenSetupWizardButton_Click' in codebehind
    assert 'Шаг 1 из 7 · Вход' in codebehind
    assert 'Начать первую запись' in codebehind


def test_meetings_support_file_drop_without_changing_import_backend_contract():
    page = (DESKTOP / "Pages" / "MeetingsPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "MeetingsPage.xaml.cs").read_text(encoding="utf-8")
    assert 'x:Name="ImportDropZone"' in page
    assert 'AllowDrop="True"' in page
    assert 'DragOver="ImportDropZone_DragOver"' in page
    assert 'Drop="ImportDropZone_Drop"' in page
    assert 'GetStorageItemsAsync' in codebehind
    assert 'ImportFileAsync(file.Path' in codebehind


def test_title_bar_statuses_explain_runtime_health():
    shell = (DESKTOP / "MainWindow.xaml").read_text(encoding="utf-8")
    assert 'ToolTipService.ToolTip="Состояние локального Recorder Agent и захвата микрофона"' in shell
    assert 'ToolTipService.ToolTip="Доступность LAN-сервера и текущей API-сессии"' in shell
    assert 'ToolTipService.ToolTip="Готовность WhisperX, GPU и воркеров обработки"' in shell
    assert 'AutomationProperties.Name="Состояние WhisperX"' in shell
    codebehind = (DESKTOP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    assert "ToolTipService.SetToolTip" in codebehind
    assert "LAN-сервер не отвечает" in codebehind
    assert "WhisperX или GPU/worker ещё не готовы" in codebehind


def test_global_recording_controller_keeps_stop_available_across_navigation():
    shell = (DESKTOP / "MainWindow.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    assert 'x:Name="GlobalRecordingController"' in shell
    assert 'x:Name="GlobalStopButton"' in shell
    assert 'Click="GlobalStopButton_Click"' in shell
    assert 'UpdateGlobalRecordingController(recorderResponse)' in codebehind
    assert 'GlobalRecordingTimer_Tick' in codebehind


def test_home_activity_card_explains_independent_background_pipeline():
    page = (DESKTOP / "Pages" / "HomePage.xaml").read_text(encoding="utf-8")
    assert 'Text="Активность"' in page
    assert 'Локальная запись, кодирование, доставка и WhisperX выполняются независимо.' in page


def test_meeting_workspace_uses_overflow_actions_and_protocol_tab():
    page = (DESKTOP / "Pages" / "MeetingsPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "MeetingsPage.xaml.cs").read_text(encoding="utf-8")
    assert 'x:Name="WorkspaceMoreButton"' in page
    assert 'MenuFlyoutItem x:Name="RetryMenuItem"' in page
    assert 'MenuFlyoutItem x:Name="DeleteMeetingMenuItem"' in page
    assert 'Header="Протокол"' in page
    assert 'WorkspaceMoreButton.IsEnabled' in codebehind


def test_meeting_decisions_and_tasks_use_human_status_labels():
    page = (DESKTOP / "Pages" / "MeetingsPage.xaml").read_text(encoding="utf-8")
    client = (DESKTOP / "ServerApiClient.cs").read_text(encoding="utf-8")
    assert 'Text="{Binding StatusText}"' in page
    assert 'public string StatusText => UiStatusMapper.Text(Status);' in client
    assert 'public string ResponsibleText' in client
    assert 'public string DeadlineText' in client


def test_recorder_liveness_is_not_confused_with_optional_audio_warning():
    protocol = (ROOT / "apps" / "recorder-agent" / "AgentIpcProtocol.cs").read_text(encoding="utf-8")
    main_window = (DESKTOP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    bootstrap = (DESKTOP / "Services" / "AgentBootstrapCoordinator.cs").read_text(encoding="utf-8")
    assert "public bool IsReachable => Health is not null || Ok" in protocol
    assert "public bool HasMicrophoneCapture" in protocol
    assert ".IsReachable" in main_window
    assert "if (!health.IsReachable || health.Health is null)" in bootstrap


def test_api_polling_has_a_bounded_timeout_without_limiting_uploads():
    client = (DESKTOP / "ServerApiClient.cs").read_text(encoding="utf-8")
    assert "ApiRequestTimeout = TimeSpan.FromSeconds(10)" in client
    assert "Timeout = ApiRequestTimeout" in client
    assert "Timeout.InfiniteTimeSpan" in client


def test_api_timeouts_become_recoverable_offline_states_and_transcript_warning_is_specific():
    client = (DESKTOP / "ServerApiClient.cs").read_text(encoding="utf-8")
    mapper = (DESKTOP / "Services" / "UiStatusMapper.cs").read_text(encoding="utf-8")
    transcripts = (DESKTOP / "ViewModels" / "TranscriptsViewModel.cs").read_text(encoding="utf-8")
    assert 'when (!cancellationToken.IsCancellationRequested)' in client
    assert '"backend_timeout"' in client
    assert 'DesktopApiException { ErrorCode: "backend_timeout" }' in mapper
    assert 'ContainsWarning(warnings, "NO_SPEECH_DETECTED")' in transcripts
    assert 'Текст доступен для чтения и экспорта.' in transcripts
