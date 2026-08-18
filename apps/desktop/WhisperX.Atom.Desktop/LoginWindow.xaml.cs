using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;
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
        ServerOriginLabel.Text = $"LAN-сервер · {services.Backend.ApiUrl}";
        UsernameBox.Text = string.IsNullOrWhiteSpace(settings.Username) ? "admin" : settings.Username;
        SetPill(LanStatusDot, LanStatusText, "LAN-сервер проверяется", "LoginNeutralBrush");
        SetPill(RecorderStatusDot, RecorderStatusText, "Recorder проверяется", "LoginNeutralBrush");
        SetPill(WhisperStatusDot, WhisperStatusText, "WhisperX проверяется", "LoginNeutralBrush");
        SetPill(VoiceStatusDot, VoiceStatusText, "Мифодий выключен", "LoginNeutralBrush");

        ConfigureTitleBar();
        if (!string.IsNullOrWhiteSpace(initialMessage)) SetStatus(initialMessage, isError: true);
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(620, 700)); } catch { }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (BusyRing.IsActive) return;
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Visibility == Visibility.Visible ? PasswordBox.Password : PasswordTextBox.Text;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            SetStatus("Укажите логин и пароль.", isError: true);
            return;
        }

        LoginButton.IsEnabled = false;
        CheckConnectionButton.IsEnabled = false;
        UsernameBox.IsEnabled = false;
        PasswordBox.IsEnabled = false;
        PasswordTextBox.IsEnabled = false;
        ShowPasswordButton.IsEnabled = false;
        BusyRing.IsActive = true;
        SetPill(LanStatusDot, LanStatusText, "LAN-сервер проверяется", "LoginNeutralBrush");
        SetPill(RecorderStatusDot, RecorderStatusText, "Recorder проверяется", "LoginNeutralBrush");
        SetPill(WhisperStatusDot, WhisperStatusText, "WhisperX проверяется", "LoginNeutralBrush");
        SetStatus("Проверяем подключение и авторизацию…", isError: false);
        try
        {
            if (!await _services.Backend.LoginAsync(_services.Backend.ApiUrl, username, password))
            {
                SetPill(LanStatusDot, LanStatusText, "LAN-сервер отклонил вход", "LoginDangerBrush");
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
            BusyRing.IsActive = false;
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
        BusyRing.IsActive = true;
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
            BusyRing.IsActive = false;
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
        StatusSurface.Visibility = Visibility.Visible;
        StatusIcon.Glyph = isError ? "\uE783" : "\uE73E";
        StatusIcon.Foreground = LoginBrush(isError ? "LoginDangerBrush" : "LoginSuccessBrush");
        StatusSurface.Background = LoginBrush(isError ? "LoginDangerSurfaceBrush" : "LoginInfoBrush");
        StatusSurface.BorderBrush = LoginBrush(isError ? "LoginDangerBorderBrush" : "LoginBorderBrush");
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
