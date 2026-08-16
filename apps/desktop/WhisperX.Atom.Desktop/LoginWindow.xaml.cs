using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;

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
        ApiUrlBox.Text = services.Backend.ApiUrl;
        if (!string.IsNullOrWhiteSpace(initialMessage)) SetStatus(initialMessage, isError: true);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(620, 700)); } catch { }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (BusyRing.IsActive) return;
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            SetStatus("Укажите логин и пароль.", isError: true);
            return;
        }

        LoginButton.IsEnabled = false;
        UsernameBox.IsEnabled = false;
        PasswordBox.IsEnabled = false;
        BusyRing.IsActive = true;
        SetStatus("Проверяем подключение и авторизацию…", isError: false);
        try
        {
            if (!await _services.Backend.LoginAsync(_services.Backend.ApiUrl, username, password))
            {
                SetStatus("Неверный логин или пароль.", isError: true);
                PasswordBox.Password = string.Empty;
                return;
            }

            AgentBootstrapStatus bootstrap;
            try
            {
                bootstrap = await _services.AgentBootstrap.EnsureAgentReadyAsync();
            }
            catch (Exception)
            {
                // Authentication is already complete. Do not trap the user in
                // this window when only the local Recorder bootstrap failed;
                // Settings can recover the Host and show its exact code.
                bootstrap = new AgentBootstrapStatus(
                    Ready: false,
                    RecorderAvailable: false,
                    OfflineEligible: _services.Backend.CanUseOffline,
                    Code: "RECORDER_BOOTSTRAP_FAILED",
                    Message: "Вход выполнен, но локальный Recorder пока недоступен.")
                { Authenticated = true };
            }
            // Authentication alone is not enough to open a recording UI. A
            // temporary LAN failure is allowed when the local Host is ready,
            // but a missing/unreachable Host must remain visible and retryable
            // instead of opening a shell that cannot record.
            if (!bootstrap.RecorderAvailable)
            {
                // API authentication is still useful when the local Agent is
                // down: the user must be able to open Settings and recover it.
                await _authenticated(bootstrap);
                Close();
                return;
            }
            await _authenticated(bootstrap);
            Close();
        }
        catch (DesktopApiException exception)
        {
            SetStatus(exception.StatusCode == 401
                ? "Неверный логин или пароль."
                : $"Сервер отклонил запрос: {exception.ErrorCode}.", isError: true);
        }
        catch (HttpRequestException)
        {
            SetStatus("Сервер недоступен. Проверьте LAN-подключение и адрес сервера.", isError: true);
        }
        catch (Exception)
        {
            SetStatus("Не удалось выполнить вход. Проверьте адрес сервера и локальное подключение.", isError: true);
        }
        finally
        {
            BusyRing.IsActive = false;
            LoginButton.IsEnabled = true;
            UsernameBox.IsEnabled = true;
            PasswordBox.IsEnabled = true;
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

    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusSurface.Visibility = Visibility.Visible;
        StatusIcon.Glyph = isError ? "\uE783" : "\uE73E";
        StatusIcon.Foreground = (Brush)Application.Current.Resources[isError ? "DangerBrush" : "AccentBrush"];
        StatusSurface.Background = (Brush)Application.Current.Resources[isError ? "DangerSurfaceBrush" : "InfoSurfaceBrush"];
        StatusSurface.BorderBrush = (Brush)Application.Current.Resources[isError ? "DangerBorderBrush" : "BorderDefaultBrush"];
    }
}
