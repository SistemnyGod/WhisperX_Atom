using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;
using Windows.Graphics;
using Windows.UI;

namespace WhisperX_Atom_Desktop;

public sealed partial class LoginWindow : Window
{
    private readonly FrontendServices _services;
    private readonly Func<AgentBootstrapStatus, Task> _authenticated;

    public LoginWindow(FrontendServices services, Func<AgentBootstrapStatus, Task> authenticated, string? initialMessage = null)
    {
        InitializeComponent();
        _services = services;
        _authenticated = authenticated;

        var settings = services.Settings.Load();
        ApiUrlBox.Text = services.Backend.ApiUrl;
        // Keep infrastructure details out of the sign-in surface. The exact
        // endpoint remains available in Settings and diagnostics after login.
        ServerOriginLabel.Text = "Подключение к рабочему серверу по защищённому каналу";
        UsernameBox.Text = string.IsNullOrWhiteSpace(settings.Username) ? "admin" : settings.Username;
        SetPill(LanStatusDot, LanStatusText, "LAN-сервер проверяется", "LoginNeutralBrush");
        SetPill(RecorderStatusDot, RecorderStatusText, "Recorder проверяется", "LoginNeutralBrush");
        SetPill(WhisperStatusDot, WhisperStatusText, "WhisperX проверяется", "LoginNeutralBrush");
        SetPill(VoiceStatusDot, VoiceStatusText, "Мифодий выключен", "LoginNeutralBrush");

        ConfigureTitleBar();
        if (!string.IsNullOrWhiteSpace(initialMessage)) SetStatus(initialMessage, isError: true);
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        ConfigureInitialWindow();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (BusyRing.IsActive) return;
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Visibility == Visibility.Visible ? PasswordBox.Password : PasswordTextBox.Text;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            SetCredentialValidation(string.IsNullOrWhiteSpace(username), string.IsNullOrWhiteSpace(password));
            SetStatus("Укажите логин и пароль.", isError: true);
            return;
        }

        LoginButton.IsEnabled = false;
        CheckConnectionButton.IsEnabled = false;
        UsernameBox.IsEnabled = false;
        PasswordBox.IsEnabled = false;
        PasswordTextBox.IsEnabled = false;
        ShowPasswordButton.IsEnabled = false;
        SetBusy(true);
        SetPill(LanStatusDot, LanStatusText, "LAN-сервер проверяется", "LoginNeutralBrush");
        SetPill(RecorderStatusDot, RecorderStatusText, "Recorder проверяется", "LoginNeutralBrush");
        SetPill(WhisperStatusDot, WhisperStatusText, "WhisperX проверяется", "LoginNeutralBrush");
        SetStatus("Проверяем подключение и авторизацию…", isError: false);
        try
        {
            if (!await _services.Backend.LoginAsync(_services.Backend.ApiUrl, username, password))
            {
                SetPill(LanStatusDot, LanStatusText, "LAN-сервер отклонил вход", "LoginDangerBrush");
                SetCredentialValidation(username: false, password: true);
                SetStatus("Неверный логин или пароль.", isError: true);
                ClearPassword();
                return;
            }

            SetPill(LanStatusDot, LanStatusText, "LAN-сервер доступен", "LoginSuccessBrush");
            SetPill(WhisperStatusDot, WhisperStatusText, "WhisperX проверяется", "LoginNeutralBrush");
            if (RememberLoginCheckBox.IsChecked == true)
            {
                _services.Settings.Save(_services.Settings.Load() with { Username = username });
            }

            AgentBootstrapStatus bootstrap;
            try
            {
                bootstrap = await _services.AgentBootstrap.EnsureAgentReadyAsync();
            }
            catch (Exception)
            {
                bootstrap = new AgentBootstrapStatus(
                    Ready: false,
                    RecorderAvailable: false,
                    OfflineEligible: _services.Backend.CanUseOffline,
                    Code: "RECORDER_BOOTSTRAP_FAILED",
                    Message: "Вход выполнен, но локальный Recorder пока недоступен.")
                { Authenticated = true };
            }

            SetPill(RecorderStatusDot, RecorderStatusText,
                bootstrap.RecorderAvailable ? "Recorder готов" : "Recorder требует проверки",
                bootstrap.RecorderAvailable ? "LoginSuccessBrush" : "LoginDangerBrush");
            SetPill(WhisperStatusDot, WhisperStatusText,
                bootstrap.ServerConnected || bootstrap.Ready ? "WhisperX готов" : "WhisperX ожидает сервер",
                bootstrap.ServerConnected || bootstrap.Ready ? "LoginSuccessBrush" : "LoginNeutralBrush");

            // Authentication alone is still useful when the local Agent is down:
            // Settings can recover it and the exact bootstrap code remains visible.
            await _authenticated(bootstrap);
            Close();
        }
        catch (DesktopApiException exception)
        {
            SetPill(LanStatusDot, LanStatusText, "LAN-сервер недоступен", "LoginDangerBrush");
            SetStatus(exception.StatusCode == 401
                ? "Неверный логин или пароль."
                : $"Сервер отклонил запрос: {exception.ErrorCode}.", isError: true);
        }
        catch (HttpRequestException)
        {
            SetPill(LanStatusDot, LanStatusText, "LAN-сервер недоступен", "LoginDangerBrush");
            SetStatus("Сервер недоступен. Проверьте LAN-подключение и адрес сервера.", isError: true);
        }
        catch (Exception)
        {
            SetPill(LanStatusDot, LanStatusText, "LAN-сервер недоступен", "LoginDangerBrush");
            SetStatus("Не удалось выполнить вход. Проверьте адрес сервера и локальное подключение.", isError: true);
        }
        finally
        {
            SetBusy(false);
            LoginButton.IsEnabled = true;
            CheckConnectionButton.IsEnabled = true;
            UsernameBox.IsEnabled = true;
            PasswordBox.IsEnabled = true;
            PasswordTextBox.IsEnabled = true;
            ShowPasswordButton.IsEnabled = true;
        }
    }

    private async void CheckConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (BusyRing.IsActive) return;
        CheckConnectionButton.IsEnabled = false;
        LoginButton.IsEnabled = false;
        SetBusy(true);
        SetPill(LanStatusDot, LanStatusText, "LAN-сервер проверяется", "LoginNeutralBrush");
        SetStatus("Проверяем доступность LAN-сервера…", isError: false);
        try
        {
            var ready = await _services.Backend.CheckReadyAsync();
            if (!ready)
            {
                SetPill(LanStatusDot, LanStatusText, "LAN-сервер не готов", "LoginDangerBrush");
                SetStatus("Сервер отвечает, но ещё не готов принимать запросы.", isError: true);
                return;
            }

            SetPill(LanStatusDot, LanStatusText, "LAN-сервер доступен", "LoginSuccessBrush");
            SetStatus("LAN-сервер доступен. Можно выполнить вход.", isError: false);
        }
        catch (HttpRequestException)
        {
            SetPill(LanStatusDot, LanStatusText, "LAN-сервер недоступен", "LoginDangerBrush");
            SetStatus("Не удалось подключиться. Проверьте LAN и адрес сервера.", isError: true);
        }
        catch (Exception ex)
        {
            SetPill(LanStatusDot, LanStatusText, "Ошибка проверки сервера", "LoginDangerBrush");
            SetStatus($"Проверка не выполнена: {UiErrorFormatter.Format(ex, "SERVER_CHECK_FAILED")}", isError: true);
        }
        finally
        {
            SetBusy(false);
            CheckConnectionButton.IsEnabled = true;
            LoginButton.IsEnabled = true;
        }
    }

    private void TogglePasswordVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Visibility == Visibility.Visible)
        {
            PasswordTextBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordTextBox.Visibility = Visibility.Visible;
            PasswordVisibilityIcon.Glyph = "\uE785";
            AutomationProperties.SetName(ShowPasswordButton, "Скрыть пароль");
            PasswordTextBox.Focus(FocusState.Programmatic);
        }
        else
        {
            PasswordBox.Password = PasswordTextBox.Text;
            PasswordTextBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordVisibilityIcon.Glyph = "\uE7B3";
            AutomationProperties.SetName(ShowPasswordButton, "Показать пароль");
            PasswordBox.Focus(FocusState.Programmatic);
        }
    }

    private void CredentialBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            LoginButton_Click(LoginButton, new RoutedEventArgs());
        }
    }

    private void CredentialBox_TextChanged(object sender, TextChangedEventArgs e) => ClearCredentialValidation();

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => ClearCredentialValidation();

    private void ClearPassword()
    {
        PasswordBox.Password = string.Empty;
        PasswordTextBox.Text = string.Empty;
    }

    private void SetPill(Ellipse dot, TextBlock text, string label, string brushKey)
    {
        text.Text = label;
        dot.Fill = LoginBrush(brushKey);
    }

    private Brush LoginBrush(string key) => (Brush)LoginRoot.Resources[key];

    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusSurface.Opacity = 1;
        StatusSurface.IsHitTestVisible = true;
        StatusIcon.Glyph = isError ? "\uE783" : "\uE73E";
        StatusIcon.Foreground = LoginBrush(isError ? "LoginDangerBrush" : "LoginSuccessBrush");
        StatusSurface.Background = LoginBrush(isError ? "LoginDangerSurfaceBrush" : "LoginInfoBrush");
        StatusSurface.BorderBrush = LoginBrush(isError ? "LoginDangerBorderBrush" : "LoginBorderBrush");
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetCredentialValidation(bool username, bool password)
    {
        UsernameBox.BorderBrush = LoginBrush(username ? "LoginDangerBrush" : "LoginInputBorderBrush");
        PasswordBox.BorderBrush = LoginBrush(password ? "LoginDangerBrush" : "LoginInputBorderBrush");
        PasswordTextBox.BorderBrush = LoginBrush(password ? "LoginDangerBrush" : "LoginInputBorderBrush");
        UsernameErrorText.Opacity = username ? 1 : 0;
        PasswordErrorText.Opacity = password ? 1 : 0;
    }

    private void ClearCredentialValidation() => SetCredentialValidation(username: false, password: false);

    private void LoginRoot_SizeChanged(object sender, SizeChangedEventArgs e) => ConfigureResponsiveLayout(e.NewSize.Width);

    private void ConfigureInitialWindow()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
            }
            var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var width = Math.Min(1080, (int)(workArea.Width * 0.90));
            var height = Math.Min(760, (int)(workArea.Height * 0.90));
            width = Math.Max(Math.Min(760, workArea.Width), width);
            height = Math.Max(Math.Min(620, workArea.Height), height);
            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(
                workArea.X + Math.Max(0, (workArea.Width - width) / 2),
                workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));
        }
        catch
        {
            // The content still adapts through the ScrollViewer if the shell
            // cannot expose display bounds on an older Windows App SDK build.
        }
    }

    private void ConfigureResponsiveLayout(double width)
    {
        var compact = width < 820;
        LoginContentGrid.Padding = compact ? new Thickness(20, 16, 20, 24) : new Thickness(36, 28, 36, 32);
        LoginCard.Padding = compact ? new Thickness(28, 26, 28, 24) : new Thickness(40, 34, 40, 30);
        LoginBackgroundImage.Stretch = compact ? Stretch.Uniform : Stretch.UniformToFill;
        LoginBackgroundImage.Opacity = compact ? 0.52 : 0.92;

        LoginStatusGrid.ColumnDefinitions.Clear();
        LoginStatusGrid.RowDefinitions.Clear();
        var cells = new FrameworkElement[] { LanStatusCell, RecorderStatusCell, WhisperStatusCell, VoiceStatusCell };
        if (compact)
        {
            LoginStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            LoginStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            LoginStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LoginStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var index = 0; index < cells.Length; index++)
            {
                Grid.SetColumn(cells[index], index % 2);
                Grid.SetRow(cells[index], index / 2);
            }
            return;
        }

        for (var index = 0; index < 4; index++) LoginStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        LoginStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < cells.Length; index++)
        {
            Grid.SetColumn(cells[index], index);
            Grid.SetRow(cells[index], 0);
        }
    }

    private void ConfigureTitleBar()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.BackgroundColor = Color.FromArgb(255, 5, 13, 29);
            titleBar.ForegroundColor = Color.FromArgb(255, 244, 247, 255);
            titleBar.InactiveBackgroundColor = Color.FromArgb(255, 5, 13, 29);
            titleBar.InactiveForegroundColor = Color.FromArgb(255, 170, 185, 208);
            titleBar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 244, 247, 255);
            titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 170, 185, 208);
        }
        catch
        {
            // Older Windows App SDK builds may not expose title-bar colors;
            // the login surface itself remains fully themed.
        }
    }
}
