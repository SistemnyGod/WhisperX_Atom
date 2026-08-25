using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class SettingsPage : Page
{
    private FrontendServices? _services;
    private CancellationTokenSource? _voiceRefreshCts;
    private DispatcherQueueTimer? _voiceBarsTimer;
    private PageLayoutMode? _lastLayoutMode;
    private readonly List<Border> _voiceBars = [];
    private readonly Brush _voiceWaitingBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray);
    private readonly Brush _voiceSpeechBrush = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
    private readonly Brush _voiceQuietBrush = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
    private readonly Brush _voiceClippingBrush = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
    private double _voiceLevelSmoothed;
    public SettingsViewModel? ViewModel { get; private set; }

    public SettingsPage()
    {
        InitializeComponent();
        InitializeVoiceBars();
        SizeChanged += SettingsPage_SizeChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (FrontendServices)e.Parameter;
        ViewModel = new SettingsViewModel(_services);
        DataContext = ViewModel;
        SettingsTabs.SelectedIndex = 0;
        _services.Updates.StateChanged += Updates_StateChanged;
        UpdateChannelComboBox.SelectedValue = _services.Settings.Load().UpdateChannel;
        CurrentBuildIdentityText.Text = _services.Updates.CurrentBuildIdentity;
        RenderUpdateState();
        UpdateApiStatusVisual();
        UpdateVoiceStatusVisual();
        UpdateStatus();
        _ = RefreshRuntimeDiagnosticsAsync();
        _voiceRefreshCts?.Cancel();
        _voiceRefreshCts = new CancellationTokenSource();
        _voiceBarsTimer?.Start();
        ApplyResponsiveLayout(ActualWidth);
        _ = RefreshVoiceLoopAsync(_voiceRefreshCts.Token);
        _ = RefreshVoiceTelemetryAsync(_voiceRefreshCts.Token);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_services is not null) _services.Updates.StateChanged -= Updates_StateChanged;
        _voiceRefreshCts?.Cancel();
        _voiceRefreshCts?.Dispose();
        _voiceRefreshCts = null;
        _voiceBarsTimer?.Stop();
        base.OnNavigatedFrom(e);
    }

    private void Updates_StateChanged() => DispatcherQueue.TryEnqueue(RenderUpdateState);

    private void RenderUpdateState()
    {
        if (_services is null) return;
        var updates = _services.Updates;
        var manifest = updates.Manifest;
        UpdateStateText.Text = updates.State switch
        {
            ClientUpdateState.Checking => "Проверяется…",
            ClientUpdateState.UpToDate => "Установлена последняя версия",
            ClientUpdateState.Available => "Доступно обновление",
            ClientUpdateState.Downloading => $"Скачивание · {updates.DownloadPercent}%",
            ClientUpdateState.ReadyToInstall => "Готово к установке",
            ClientUpdateState.BlockedRecording => "Заблокировано активной записью",
            ClientUpdateState.UpdateBlocked => "Заблокировано: состояние Recorder неизвестно",
            ClientUpdateState.Incompatible => "Несовместимое обновление",
            ClientUpdateState.Installing => "Установка запущена…",
            ClientUpdateState.Deferred => "Отложено",
            ClientUpdateState.Failed => "Ошибка обновления",
            _ => "Проверка не выполнялась"
        };
        AvailableUpdatePanel.Visibility = manifest is null || updates.State is ClientUpdateState.UpToDate or ClientUpdateState.Idle
            ? Visibility.Collapsed : Visibility.Visible;
        if (manifest is null) return;
        AvailableUpdateText.Text = $"Доступна версия {manifest.Version}";
        AvailableUpdateIdentityText.Text = manifest.BuildIdentity;
        ToolTipService.SetToolTip(AvailableUpdateIdentityText, manifest.BuildIdentity);
        UpdateReleaseNotesText.Text = manifest.ReleaseNotes is { Count: > 0 } ? string.Join(" · ", manifest.ReleaseNotes) : "Описание изменений не опубликовано.";
        var pilotUnsigned = string.Equals(manifest.Channel, "pilot", StringComparison.OrdinalIgnoreCase) && !manifest.Package.AuthenticodeRequired;
        UpdateWarningText.Text = pilotUnsigned ? "Локальная тестовая сборка не имеет цифровой подписи. SHA256 проверяется; установка потребует подтверждения администратора." : string.Empty;
        UpdateWarningText.Visibility = pilotUnsigned ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.Visibility = updates.State == ClientUpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.Value = updates.DownloadPercent;
        UpdateProgressRing.IsActive = updates.State == ClientUpdateState.Downloading;
        DownloadUpdateButton.IsEnabled = updates.State is ClientUpdateState.Available or ClientUpdateState.Failed;
        CancelUpdateButton.Visibility = updates.State == ClientUpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = updates.State == ClientUpdateState.ReadyToInstall;
        DeferUpdateButton.Visibility = manifest.Mandatory ? Visibility.Collapsed : Visibility.Visible;
        UpdateErrorText.Text = updates.ErrorCode ?? string.Empty;
        UpdateErrorText.Visibility = string.IsNullOrWhiteSpace(updates.ErrorCode) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        await _services.Updates.CheckAsync();
        RenderUpdateState();
    }

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        var progress = new Progress<int>(value =>
        {
            UpdateProgressBar.Value = value;
            UpdateStateText.Text = $"Скачивание · {value}%";
        });
        await _services.Updates.DownloadAsync(progress);
        RenderUpdateState();
    }

    private void CancelUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        _services?.Updates.CancelDownload();
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        var manifest = _services.Updates.Manifest;
        var unsignedPilot = manifest is not null
            && string.Equals(manifest.Channel, "pilot", StringComparison.OrdinalIgnoreCase)
            && !manifest.Package.AuthenticodeRequired;
        var allowUnsignedPilot = false;
        if (unsignedPilot)
        {
            var confirmation = new ContentDialog
            {
                Title = "Установить локальный pilot?",
                Content = "Локальная тестовая сборка не имеет цифровой подписи. SHA256 и build identity будут проверены, после чего Windows запросит подтверждение администратора.",
                PrimaryButtonText = "Установить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            allowUnsignedPilot = await confirmation.ShowAsync() == ContentDialogResult.Primary;
            if (!allowUnsignedPilot) return;
        }
        await _services.Updates.InstallAsync(allowUnsignedPilot);
        RenderUpdateState();
    }

    private void DeferUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        _services?.Updates.Defer();
        RenderUpdateState();
    }

    private void UpdateChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_services is null || UpdateChannelComboBox.SelectedValue is not string channel) return;
        _services.Settings.Save(_services.Settings.Load() with { UpdateChannel = channel });
    }

    private async void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || SettingsTabs.SelectedIndex != 3) return;
        await ViewModel.RefreshRecorderDiagnosticsAsync();
        await ViewModel.RefreshVoiceDiagnosticsAsync();
        UpdateVoiceStatusVisual();
        UpdateStatus();
    }

    private async void RefreshDiagnosticsTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshRecorderDiagnosticsAsync();
        await ViewModel.RefreshVoiceDiagnosticsAsync();
        UpdateVoiceStatusVisual();
        UpdateStatus();
    }

    private async Task RefreshVoiceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RefreshVoiceDiagnosticsAsync(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { }
            UpdateApiStatusVisual();
            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RefreshRuntimeDiagnosticsAsync()
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshRecorderDiagnosticsAsync();
    }

    private async Task RefreshVoiceDiagnosticsAsync()
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshVoiceDiagnosticsAsync();
        UpdateVoiceStatusVisual();
    }

    private async Task RefreshVoiceTelemetryAsync(CancellationToken cancellationToken)
    {
        if (_services is null) return;
        var client = new WhisperX.Atom.Desktop.VoiceHostClient();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // The telemetry pipe is disposable: Voice Host may restart,
                // or Settings may open before it is ready. Reconnect after
                // both failures and a clean disconnect.
                await client.SubscribeTelemetryAsync(packet =>
                {
                    if (ViewModel is not null)
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            ViewModel.ApplyVoiceTelemetry(packet);
                        });
                    return Task.CompletedTask;
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch
            {
                // Status polling remains the compatibility fallback while
                // the next connection attempt waits for Voice Host startup.
                ViewModel?.MarkVoiceTelemetryDisconnected();
            }
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    private void InitializeVoiceBars()
    {
        for (var index = 0; index < 24; index++)
        {
            VoiceBarsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bar = new Border
            {
                Width = 5,
                Height = 4,
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = _voiceWaitingBrush
            };
            Grid.SetColumn(bar, index);
            VoiceBarsPanel.Children.Add(bar);
            _voiceBars.Add(bar);
        }

        _voiceBarsTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _voiceBarsTimer.Interval = TimeSpan.FromMilliseconds(33);
        _voiceBarsTimer.Tick += (_, _) => RenderVoiceBars();
    }

    private void RenderVoiceBars()
    {
        if (ViewModel is null || _voiceBars.Count == 0) return;
        ViewModel.RefreshVoiceTelemetryStaleness();
        var target = Math.Clamp(ViewModel.VoiceLevelNormalized, 0d, 1d);
        _voiceLevelSmoothed += (target - _voiceLevelSmoothed) * 0.22d;
        if (target == 0 && _voiceLevelSmoothed < 0.01) _voiceLevelSmoothed = 0;
        var brush = ViewModel.VoiceSignalState.Contains("Перегрузка", StringComparison.OrdinalIgnoreCase)
            ? _voiceClippingBrush
            : ViewModel.VoiceSignalState.Contains("Голос", StringComparison.OrdinalIgnoreCase)
                ? _voiceSpeechBrush
                : ViewModel.VoiceSignalState.Contains("Тишина", StringComparison.OrdinalIgnoreCase)
                    ? _voiceQuietBrush
                    : _voiceWaitingBrush;
        var center = (_voiceBars.Count - 1) / 2d;
        var radius = Math.Max(1d, center);
        for (var index = 0; index < _voiceBars.Count; index++)
        {
            var symmetry = 1d - Math.Abs(index - center) / radius;
            var height = 4d + (48d * _voiceLevelSmoothed * (0.55d + 0.45d * symmetry));
            _voiceBars[index].Height = Math.Clamp(height, 4d, 54d);
            _voiceBars[index].Background = brush;
        }
    }

    private async void TestVoiceSpeechButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.TestVoiceSpeechAsync("Мифодий, начни запись");
        UpdateVoiceStatusVisual();
        UpdateStatus();
    }

    private async void TestVoiceTtsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.TestVoiceTtsAsync();
        UpdateVoiceStatusVisual();
        UpdateStatus();
    }

    private async void CalibrateVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.CalibrateVoiceNoiseAsync();
        UpdateVoiceStatusVisual();
        UpdateStatus();
    }

    private async void RefreshVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshVoiceDiagnosticsAsync();
        UpdateStatus();
    }

    private void LoginAgainButton_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).RequestLogin("Сессия API истекла. Выполните вход повторно.");
    }

    private void UpdateVoiceStatusVisual()
    {
        if (ViewModel is null) return;
        var status = ViewModel.VoiceStatus ?? string.Empty;
        var errorCode = ViewModel.VoiceErrorCode ?? string.Empty;
        var failed = status.Contains("ошиб", StringComparison.OrdinalIgnoreCase)
            || status.Contains("недоступ", StringComparison.OrdinalIgnoreCase)
            || status.Contains("нет heartbeat", StringComparison.OrdinalIgnoreCase)
            || errorCode.StartsWith("VOICE_", StringComparison.OrdinalIgnoreCase)
            || errorCode.Contains("MISMATCH", StringComparison.OrdinalIgnoreCase);
        var ready = status.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)
            || status.Contains("слушает", StringComparison.OrdinalIgnoreCase)
            || status.Contains("готов", StringComparison.OrdinalIgnoreCase);
        var brushKey = failed ? "DangerBrush" : ready ? "SuccessBrush" : "WarningBrush";
        var surfaceKey = failed ? "DangerSurfaceBrush" : ready ? "SurfaceGreenBrush" : "SurfaceOrangeBrush";
        if (Application.Current.Resources[brushKey] is Brush brush)
        {
            MifodiyStatusDot.Fill = brush;
            MifodiyStatusText.Foreground = brush;
            MifodiyStatusBadge.BorderBrush = brush;
        }
        if (Application.Current.Resources[surfaceKey] is Brush surface)
            MifodiyStatusBadge.Background = surface;
    }

    private void OpenRecordingButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("recording");

    private async void OpenSetupWizardButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || _services is null) return;

        var recorderReady = false;
        var microphoneName = "Не выбран";
        var storageText = "Нет данных";
        var serverReady = false;
        var processingReady = false;
        try
        {
            await ViewModel.RefreshRecorderDiagnosticsAsync();
            await ViewModel.RefreshVoiceDiagnosticsAsync();
            var recorder = await _services.Recorder.GetHealthAsync();
            recorderReady = recorder.IsReachable;
            microphoneName = recorder.Health?.EffectiveMicrophoneDeviceName
                ?? recorder.Health?.SelectedMicrophoneDeviceId
                ?? "Windows по умолчанию";
            if (recorder.Health is { FreeBytes: > 0 } health)
                storageText = FormatBytes(health.FreeBytes);
            if (_services.Backend.HasSession)
            {
                serverReady = await _services.Backend.CheckReadyAsync();
                processingReady = (await _services.Backend.GetProcessingReadinessAsync())?.Ready == true;
            }
        }
        catch
        {
            // The wizard remains useful offline: it reports the failed step and
            // lets the user continue to the recording page for recovery.
        }

        var wizardBody = new StackPanel { Spacing = 12, MinWidth = 420 };
        var stepProgress = new ProgressBar { Minimum = 0, Maximum = 7, Height = 6 };
        var stepTitle = new TextBlock { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var stepDescription = new TextBlock { Foreground = GetBrush("MutedTextBrush"), TextWrapping = TextWrapping.Wrap };
        var checks = new StackPanel { Spacing = 7 };
        wizardBody.Children.Add(stepProgress);
        wizardBody.Children.Add(stepTitle);
        wizardBody.Children.Add(stepDescription);
        wizardBody.Children.Add(checks);

        var dialog = new ContentDialog
        {
            Title = "Настройка WhisperX Atom",
            Content = wizardBody,
            PrimaryButtonText = "Далее",
            SecondaryButtonText = "Назад",
            CloseButtonText = "Закрыть",
            XamlRoot = Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };
        var step = 0;

        void RenderStep()
        {
            stepProgress.Value = step + 1;
            checks.Children.Clear();
            var title = step switch
            {
                0 => "Шаг 1 из 7 · Вход",
                1 => "Шаг 2 из 7 · LAN-сервер",
                2 => "Шаг 3 из 7 · Recorder Host",
                3 => "Шаг 4 из 7 · Микрофон",
                4 => "Шаг 5 из 7 · Голосовой помощник",
                5 => "Шаг 6 из 7 · Локальное хранилище",
                _ => "Готово к работе"
            };
            stepTitle.Text = title;
            stepDescription.Text = step switch
            {
                0 => ViewModel.IsLoggedIn ? "Вход в API уже выполнен." : "Выполните вход через окно авторизации приложения.",
                1 => "Сервер нужен для доставки и обработки. Локальная запись доступна и без сети.",
                2 => "Проверяем установленный Recorder Host и его IPC.",
                3 => "Устройство будет выбрано на странице записи; до старта аудио не сохраняется.",
                4 => "Мифодий остаётся экспериментальной функцией и не блокирует запись.",
                5 => "Папка архива используется для локального master-файла и raw-сегментов.",
                _ => "Основной сценарий готов: выберите микрофон и начните первую запись."
            };
            if (step == 0) AddWizardCheck("Пользователь", ViewModel.IsLoggedIn ? "Вход выполнен" : "Требуется вход", ViewModel.IsLoggedIn);
            if (step == 1) AddWizardCheck("LAN-сервер", serverReady ? "Доступен" : "Офлайн или вход не выполнен", serverReady);
            if (step == 2) AddWizardCheck("Recorder Host", recorderReady ? ViewModel.RecorderRuntimeState : "Недоступен", recorderReady);
            if (step == 3) AddWizardCheck("Микрофон", microphoneName, recorderReady && microphoneName is not "Не выбран");
            if (step == 4) AddWizardCheck("Мифодий", ViewModel.VoiceStatus, false, "Проверка доступна в экспериментальных функциях");
            if (step == 5) AddWizardCheck("Локальное место", storageText, storageText is not "Нет данных");
            if (step == 6)
            {
                AddWizardCheck("Recorder Host", recorderReady ? "Готов" : "Требует проверки", recorderReady);
                AddWizardCheck("Микрофон", microphoneName, recorderReady && microphoneName is not "Не выбран");
                AddWizardCheck("Локальное хранение", storageText, storageText is not "Нет данных");
                AddWizardCheck("LAN-сервер", serverReady ? "Доступен" : "Будет доставлено позже", serverReady, serverReady ? null : "Можно продолжить в offline-режиме");
                AddWizardCheck("WhisperX", processingReady ? "Готов" : "Ожидает сервер/GPU", processingReady, processingReady ? null : "Не блокирует локальную запись");
            }
            dialog.PrimaryButtonText = step == 6 ? "Начать первую запись" : "Далее";
            dialog.IsSecondaryButtonEnabled = step > 0;
        }

        void AddWizardCheck(string label, string value, bool success, string? hint = null)
        {
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } }, ColumnSpacing = 12 };
            row.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var result = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(result, 1);
            result.Children.Add(new TextBlock { Text = (success ? "✓ " : "⚠ ") + value, Foreground = GetBrush(success ? "SuccessBrush" : "WarningBrush"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right });
            if (!string.IsNullOrWhiteSpace(hint)) result.Children.Add(new TextBlock { Text = hint, Foreground = GetBrush("MutedTextBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right });
            row.Children.Add(result);
            checks.Children.Add(row);
        }

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (step < 6)
            {
                step++;
                RenderStep();
                args.Cancel = true;
            }
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            if (step > 0)
            {
                step--;
                RenderStep();
                args.Cancel = true;
            }
        };
        RenderStep();
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && step == 6)
            App.MainWindow.NavigateTo("recording");
    }

    private static Brush GetBrush(string key) => Application.Current.Resources[key] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        var value = (double)bytes;
        var units = new[] { "КБ", "МБ", "ГБ", "ТБ" };
        var index = -1;
        do { value /= 1024; index++; } while (value >= 1024 && index < units.Length - 1);
        return $"{value:0.#} {units[index]} свободно";
    }

    private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.ChangePasswordAsync(PasswordBox.Password, NewPasswordBox.Password);
        PasswordBox.Password = string.Empty;
        NewPasswordBox.Password = string.Empty;
        UpdateStatus();
    }

    private async void CheckBackendButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.CheckBackendAsync();
        UpdateStatus();
    }

    private async void ApplyServerOriginButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.ApplyServerOriginAsync();
        UpdateStatus();
    }

    private async void ReconnectAgentButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var confirmation = new ContentDialog
        {
            Title = "Переподключить Recorder Agent?",
            Content = "Токен будет перевыпущен только для этого явного действия администратора. Текущий токен Agent перестанет работать.",
            PrimaryButtonText = "Переподключить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        await ViewModel.ReconnectAgentAsync();
        UpdateStatus();
    }

    private async void StartRecorderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.StartRecorderServiceAsync();
        await ViewModel.RefreshRecorderDiagnosticsAsync();
        UpdateStatus();
    }

    private async void SaveDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.SaveDiagnosticsAsync();
        UpdateStatus();
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.LogoutAsync();
        UpdateStatus();
    }

    private async void SelectArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, App.MainWindow.GetWindowHandle());
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        await ViewModel.SetArchiveRootAsync(folder.Path);
        UpdateStatus();
    }

    private void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{ViewModel.ArchiveRoot}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось открыть папку архива.")); }
    }

    private void UpdateStatus()
    {
        if (ViewModel is null) return;
        UpdateApiStatusVisual();
        LoginHintText.Visibility = ViewModel.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        PasswordFieldsPanel.Visibility = ViewModel.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        ChangePasswordButton.Visibility = ViewModel.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        LoginAgainButton.Visibility = ViewModel.IsSessionExpired ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(ViewModel.StatusText)) return;
        StatusInfoBar.Severity = ViewModel.StatusText.Contains("ошиб", StringComparison.OrdinalIgnoreCase) || ViewModel.StatusText.Contains("не ", StringComparison.OrdinalIgnoreCase)
            ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        StatusInfoBar.Message = ViewModel.StatusText;
        StatusInfoBar.IsOpen = true;
    }

    private void ShowError(string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private void UpdateApiStatusVisual()
    {
        if (_services is null) return;
        var (brushKey, surfaceKey) = _services.Backend.AuthState switch
        {
            DesktopAuthState.Authenticated => ("SuccessBrush", "SurfaceGreenBrush"),
            DesktopAuthState.Offline => ("WarningBrush", "SurfaceOrangeBrush"),
            DesktopAuthState.LoginRequired => ("DangerBrush", "DangerSurfaceBrush"),
            _ => ("AccentBrush", "SurfaceBlueBrush")
        };
        if (Application.Current.Resources[brushKey] is Brush brush)
        {
            ApiStatusBadge.BorderBrush = brush;
            ApiStatusBadgeText.Foreground = brush;
        }
        if (Application.Current.Resources[surfaceKey] is Brush surface)
            ApiStatusBadge.Background = surface;
    }

    private void SettingsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        var mode = ResponsiveLayout.GetMode(width);
        if (_lastLayoutMode == mode) return;
        _lastLayoutMode = mode;
        ResponsiveLayout.SetTwoColumn(SettingsLayoutGrid, ApiSettingsCard, AgentSettingsCard, 380, width, allowStandard: true);
        ResponsiveLayout.SetCardColumns(MifodiyPrimaryGrid, new FrameworkElement?[] { MifodiySignalCard, MifodiyControlsCard }, width, 2);
        SettingsTabs.TabWidthMode = mode == PageLayoutMode.Compact
            ? TabViewWidthMode.SizeToContent
            : TabViewWidthMode.Equal;
        ApiActionsPanel.Orientation = mode == PageLayoutMode.Compact
            ? Orientation.Vertical
            : Orientation.Horizontal;
        MifodiyTestActionsPanel.Orientation = mode == PageLayoutMode.Compact
            ? Orientation.Vertical
            : Orientation.Horizontal;
        ConfigureArchiveLayout(mode == PageLayoutMode.Compact);
    }

    private void ConfigureArchiveLayout(bool compact)
    {
        ArchivePathGrid.ColumnDefinitions.Clear();
        ArchivePathGrid.RowDefinitions.Clear();
        if (compact)
        {
            ArchivePathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < 3; index++) ArchivePathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(SelectArchiveButton, 0);
            Grid.SetRow(SelectArchiveButton, 1);
            Grid.SetColumn(OpenArchiveButton, 0);
            Grid.SetRow(OpenArchiveButton, 2);
            return;
        }

        ArchivePathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ArchivePathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ArchivePathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ArchivePathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(SelectArchiveButton, 1);
        Grid.SetRow(SelectArchiveButton, 0);
        Grid.SetColumn(OpenArchiveButton, 2);
        Grid.SetRow(OpenArchiveButton, 0);
    }
}
