using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
        if (!string.IsNullOrWhiteSpace(initialMessage)) StatusText.Text = initialMessage;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 560)); } catch { }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (BusyRing.IsActive) return;
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            StatusText.Text = "Укажите логин и пароль.";
            return;
        }

        LoginButton.IsEnabled = false;
        BusyRing.IsActive = true;
        StatusText.Text = "Подключение к серверу…";
        try
        {
            if (!await _services.Backend.LoginAsync(_services.Backend.ApiUrl, username, password))
            {
                StatusText.Text = "Неверный логин или пароль.";
                PasswordBox.Password = string.Empty;
                return;
            }

            var bootstrap = await _services.AgentBootstrap.EnsureAgentReadyAsync();
            // Authentication alone is not enough to open a recording UI. A
            // temporary LAN failure is allowed when the local Host is ready,
            // but a missing/unreachable Host must remain visible and retryable
            // instead of opening a shell that cannot record.
            if (!bootstrap.RecorderAvailable)
            {
                StatusText.Text = $"{bootstrap.Code}: {bootstrap.Message}";
                return;
            }
            await _authenticated(bootstrap);
            Close();
        }
        catch (DesktopApiException exception)
        {
            StatusText.Text = exception.StatusCode == 401
                ? "Неверный логин или пароль."
                : $"Сервер отклонил запрос: {exception.ErrorCode}.";
        }
        catch (HttpRequestException)
        {
            StatusText.Text = "Сервер недоступен. Проверьте LAN-подключение.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Не удалось выполнить вход: {exception.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
            LoginButton.IsEnabled = true;
        }
    }
}
