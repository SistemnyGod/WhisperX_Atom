using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.ComponentModel;
using Microsoft.Win32;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Desktop;

public partial class MainWindow : Window
{
    private readonly AgentPipeClient _agent = new();
    private ServerApiClient _server = new();
    private readonly DispatcherTimer _timer;
    private DesktopMeeting? _selectedMeeting;
    private IReadOnlyList<DesktopSpeaker> _speakers = [];
    private DesktopTask? _selectedTask;
    private bool _updatingSlider;
    private CancellationTokenSource? _processingPollCts;
    private CancellationTokenSource? _detailsCts;
    private ICollectionView? _meetingsView;
    private int _statusRefreshGate;

    public MainWindow()
    {
        InitializeComponent();
        var settings = DesktopSettings.Load();
        ApiUrlTextBox.Text = settings.ApiUrl;
        UsernameTextBox.Text = settings.Username;
        _server.Dispose();
        _server = new ServerApiClient(settings.ApiUrl);
        _server.RestoreSession(settings.UnprotectSessionCookie());
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) =>
        {
            await RefreshStatusAsync();
            UpdatePlayerPosition();
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            _ = LoadMeetingsAsync();
            _ = RefreshStatusAsync();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.I && ImportButton.IsEnabled)
        {
            ImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        await RefreshStatusAsync();
        await CheckServerAsync();
        if (!string.IsNullOrWhiteSpace(_server.GetSessionCookie()))
            await LoadMeetingsAsync();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _processingPollCts?.Cancel();
        _processingPollCts?.Dispose();
        _processingPollCts = null;
        _detailsCts?.Cancel();
        _detailsCts?.Dispose();
        _detailsCts = null;
        PreviewPlayer.Stop();
        _server.Dispose();
    }

    private async Task RefreshStatusAsync()
    {
        if (Interlocked.Exchange(ref _statusRefreshGate, 1) == 1) return;
        try
        {
            var status = await _agent.SendAsync("HEALTH");
            AgentStatusText.Text = status.Ok ? "Подключён" : "Недоступен";
            AgentStatusText.Foreground = status.Ok ? Brushes.LightGreen : Brushes.OrangeRed;
            RecordingStateText.Text = status.State switch
            {
                "Recording" => "Идёт запись",
                "Paused" => "Пауза",
                "Idle" => "Ожидание",
                _ => status.State,
            };
            SessionText.Text = string.IsNullOrWhiteSpace(status.SessionId) ? "Сессия не создана" : $"Сессия: {status.SessionId}";
            UpdateRecordingControls(status.Ok, status.State);
            if (status.Health is not null)
            {
                MicrophoneText.Text = $"Микрофон: {(status.Health.Microphone ? "работает" : "не найден")} ({status.Health.CaptureDeviceCount} устройств)";
                SystemAudioText.Text = $"Системный звук: {(status.Health.SystemAudio ? "работает" : "не найден")} ({status.Health.RenderDeviceCount} устройств)";
                StorageText.Text = $"Диск: {FormatBytes(status.Health.FreeBytes)} свободно из {FormatBytes(status.Health.TotalBytes)}";
                DeviceErrorText.Text = status.Health.Error ?? string.Empty;
            }
            LastErrorText.Text = status.Error ?? string.Empty;
            FooterText.Text = $"Последняя проверка: {DateTime.Now:T}";
        }
        catch (Exception ex)
        {
            AgentStatusText.Text = "Недоступен";
            AgentStatusText.Foreground = Brushes.OrangeRed;
            RecordingStateText.Text = "Сервис недоступен";
            LastErrorText.Text = SafeError(ex);
            FooterText.Text = "Recorder Service не отвечает";
            UpdateRecordingControls(false, "Unavailable");
        }
        finally
        {
            Volatile.Write(ref _statusRefreshGate, 0);
        }
    }

    private async Task CheckServerAsync()
    {
        try
        {
            var ready = await _server.CheckReadyAsync();
            ServerStatusText.Text = ready ? "Готов" : "Недоступен";
            ServerStatusText.Foreground = ready ? Brushes.LightGreen : Brushes.OrangeRed;
            AdminStatusText.Text = ready ? $"Локальный API подключён: {_server.BaseAddress}" : "Локальный API недоступен.";
        }
        catch (Exception ex)
        {
            ServerStatusText.Text = "Недоступен";
            ServerStatusText.Foreground = Brushes.OrangeRed;
            AdminStatusText.Text = SafeError(ex);
        }
    }

    private async Task<AgentIpcResponse?> RunCommandAsync(string command, object? payload = null)
    {
        try
        {
            var response = await _agent.SendAsync(command, payload);
            if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Команда отклонена агентом.");
            FooterText.Text = $"Команда {command} выполнена";
            await RefreshStatusAsync();
            return response;
        }
        catch (Exception ex)
        {
            LastErrorText.Text = SafeError(ex);
            FooterText.Text = $"Команда {command} завершилась ошибкой";
            return null;
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        Guid? meetingId = Guid.TryParse(_selectedMeeting?.Id, out var id) ? id : null;
        var title = string.IsNullOrWhiteSpace(RecordingTitleBox.Text)
            ? _selectedMeeting?.Title
            : RecordingTitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = $"Совещание {DateTime.Now:dd.MM.yyyy HH:mm}";
        var response = await RunCommandAsync("START", new { meetingId, title });
        if (response?.MeetingId is Guid startedMeetingId)
        {
            ProcessingStatusText.Text = $"Запись: {title} · совещание {startedMeetingId}";
            ProcessingErrorText.Text = response.Error == "server_binding_pending" ? "Сервер временно недоступен; запись продолжается локально." : string.Empty;
        }
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e) => await RunCommandAsync("PAUSE");
    private async void ResumeButton_Click(object sender, RoutedEventArgs e) => await RunCommandAsync("RESUME");

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Подтвердить завершение записи?", "WhisperX Atom", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var response = await RunCommandAsync("STOP");
        if (response?.MeetingId is Guid meetingId)
            _ = TrackProcessingAsync(meetingId);
        else
            ProcessingStatusText.Text = "Запись завершена локально; серверное совещание ещё не связано.";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _server.Dispose();
            _server = new ServerApiClient(ApiUrlTextBox.Text);
            var ok = await _server.LoginAsync(UsernameTextBox.Text, PasswordBox.Password);
            AdminStatusText.Text = ok ? "Вход в локальный API выполнен." : "Ошибка входа.";
            if (ok)
            {
                DesktopSettings.Save(_server.BaseAddress.ToString(), UsernameTextBox.Text, _server.GetSessionCookie());
                await LoadMeetingsAsync();
            }
        }
        catch (Exception ex)
        {
            AdminStatusText.Text = SafeError(ex);
        }
    }

    private async void CheckServerButton_Click(object sender, RoutedEventArgs e) => await CheckServerAsync();

    private void UpdateRecordingControls(bool connected, string state)
    {
        StartRecordingButton.IsEnabled = connected && string.Equals(state, "Idle", StringComparison.OrdinalIgnoreCase);
        PauseRecordingButton.IsEnabled = connected && string.Equals(state, "Recording", StringComparison.OrdinalIgnoreCase);
        ResumeRecordingButton.IsEnabled = connected && string.Equals(state, "Paused", StringComparison.OrdinalIgnoreCase);
        StopRecordingButton.IsEnabled = connected && (string.Equals(state, "Recording", StringComparison.OrdinalIgnoreCase) || string.Equals(state, "Paused", StringComparison.OrdinalIgnoreCase));
    }

    private async void DockerStatusButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var status = await _server.GetSystemStatusAsync();
            AdminStatusText.Text = status is null
                ? "Статус сервера недоступен. Выполните вход в API."
                : $"API: {(status.Ready ? "готов" : "ошибка")}; PostgreSQL: {(status.Postgres ? "подключён" : "недоступен")}; диск: {FormatBytes(status.FreeBytes)} свободно из {FormatBytes(status.TotalBytes)}";
        }
        catch (Exception ex)
        {
            AdminStatusText.Text = SafeError(ex);
        }
    }
    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = Environment.GetEnvironmentVariable("WHISPERX_DATA_HOST") ?? @"C:\WhisperXAtom\Data";
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void OpenAgentSpoolButton_Click(object sender, RoutedEventArgs e)
    {
        var path = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
    private async void EnrollAgentButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await _server.CheckReadyAsync())
            {
                AdminStatusText.Text = "Сначала подключите локальный API.";
                return;
            }
            if (string.IsNullOrWhiteSpace(AgentNameBox.Text) || string.IsNullOrWhiteSpace(EnrollmentSecretBox.Password))
            {
                AdminStatusText.Text = "Укажите имя агента и enrollment secret.";
                return;
            }
            var enrollment = await _server.EnrollAgentAsync(AgentNameBox.Text.Trim(), EnrollmentSecretBox.Password);
            var configured = await _agent.SendAsync("CONFIGURE", new
            {
                serverUrl = _server.BaseAddress.ToString().TrimEnd('/'),
                agentId = enrollment.AgentId,
                token = enrollment.Token,
            });
            AdminStatusText.Text = configured.Ok
                ? "Recorder Service зарегистрирован и настроен в API."
                : $"Ошибка регистрации, но команда IPC не выполнена: {configured.Error}";
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            AdminStatusText.Text = SafeError(ex);
        }
    }

    private async void LoadMeetingsButton_Click(object sender, RoutedEventArgs e) => await LoadMeetingsAsync();

    private async void ImportFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импортировать аудио или видео",
            Filter = "Медиафайлы|*.wav;*.flac;*.mp3;*.m4a;*.aac;*.ogg;*.opus;*.mp4;*.mkv;*.mov;*.webm;*.avi|Все файлы|*.*",
            Multiselect = false,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ImportStatusText.Text = $"Загрузка: {Path.GetFileName(dialog.FileName)}";
            ImportStatusText.Foreground = Brushes.LightSkyBlue;
            ImportButton.IsEnabled = false;
            var progress = new Progress<long>(bytes => ImportStatusText.Text = $"Файл загружен: {FormatBytes(bytes)}");
            var meeting = await _server.ImportFileAsync(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName), progress);
            ImportStatusText.Text = "Файл принят. Запущена обработка.";
            ImportStatusText.Foreground = Brushes.LightGreen;
            await LoadMeetingsAsync();
            var item = MeetingsList.Items.OfType<DesktopMeeting>().FirstOrDefault(x => x.Id == meeting.Id);
            if (item is not null)
            {
                MeetingsList.SelectedItem = item;
                _ = TrackProcessingAsync(Guid.Parse(item.Id));
            }
        }
        catch (Exception ex)
        {
            ImportStatusText.Text = SafeError(ex);
            ImportStatusText.Foreground = Brushes.OrangeRed;
        }
        finally { ImportButton.IsEnabled = true; }
    }

    private async void CreateMeetingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var title = NewMeetingTitleBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(title) || title == "Название совещания")
            {
                AdminStatusText.Text = "Укажите название совещания.";
                return;
            }
            var meeting = await _server.CreateMeetingAsync(title);
            await LoadMeetingsAsync();
            foreach (var item in MeetingsList.Items.OfType<DesktopMeeting>())
            {
                if (item.Id == meeting.Id) { MeetingsList.SelectedItem = item; break; }
            }
            FooterText.Text = "Совещание создано; его можно связать с записью.";
        }
        catch (Exception ex)
        {
            AdminStatusText.Text = SafeError(ex);
        }
    }

    private async Task LoadMeetingsAsync()
    {
        try
        {
            var meetings = await _server.GetMeetingsAsync();
            _meetingsView = CollectionViewSource.GetDefaultView(meetings);
            _meetingsView.Filter = FilterMeeting;
            MeetingsList.ItemsSource = _meetingsView;
            MeetingCountText.Text = $"Совещаний: {meetings.Count}";
            FooterText.Text = $"Совещаний загружено: {meetings.Count}";
        }
        catch (Exception ex)
        {
            AdminStatusText.Text = SafeError(ex);
        }
    }

    private bool FilterMeeting(object item)
    {
        if (item is not DesktopMeeting meeting) return false;
        var query = MeetingSearchBox.Text.Trim();
        return string.IsNullOrWhiteSpace(query)
            || meeting.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || meeting.Status.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void MeetingSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _meetingsView?.Refresh();
        if (_meetingsView is not null) MeetingCountText.Text = $"Показано: {MeetingsList.Items.Count}";
    }
    private async void MeetingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MeetingsList.SelectedItem is not DesktopMeeting meeting) return;
        _detailsCts?.Cancel();
        _detailsCts?.Dispose();
        _detailsCts = new CancellationTokenSource();
        _selectedMeeting = meeting;
        RecordingTitleBox.Text = meeting.Title;
        SelectedMeetingTitle.Text = meeting.Title;
        SelectedMeetingStatus.Text = $"{meeting.Status} · {meeting.CreatedAt.ToLocalTime():g}";
        await LoadMeetingDetailsAsync(meeting, _detailsCts.Token);
    }

    private async Task LoadMeetingDetailsAsync(DesktopMeeting meeting, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(meeting.Id, out var meetingId)) return;
        TranscriptStatusText.Text = "Загрузка результатов…";
        SummaryTextBox.Text = "Загрузка саммари…";
        try
        {
            var transcript = await _server.GetTranscriptAsync(meetingId, cancellationToken);
            TranscriptList.ItemsSource = transcript?.Segments ?? [];
            TranscriptStatusText.Text = transcript is null ? "Стенограмма пока не готова." : $"Статус: {transcript.Status}; сегментов: {transcript.Segments.Count}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { TranscriptStatusText.Text = $"Стенограмма: {SafeError(ex)}"; TranscriptList.ItemsSource = Array.Empty<DesktopTranscriptSegment>(); }

        try
        {
            _speakers = await _server.GetSpeakersAsync(meetingId, cancellationToken);
            SpeakerList.ItemsSource = _speakers;
            MergeSourceBox.ItemsSource = _speakers;
            MergeTargetBox.ItemsSource = _speakers;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { SpeakerList.ItemsSource = Array.Empty<DesktopSpeaker>(); FooterText.Text = $"Спикеры: {SafeError(ex)}"; }

        try { SummaryTextBox.Text = FormatSummary(await _server.GetSummaryAsync(meetingId, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { SummaryTextBox.Text = $"Саммари: {SafeError(ex)}"; }
        try { DecisionList.ItemsSource = await _server.GetDecisionsAsync(meetingId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { DecisionList.ItemsSource = Array.Empty<DesktopDecision>(); FooterText.Text = $"Решения: {SafeError(ex)}"; }
        try { TaskList.ItemsSource = await _server.GetTasksAsync(meetingId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { TaskList.ItemsSource = Array.Empty<DesktopTask>(); FooterText.Text = $"Поручения: {SafeError(ex)}"; }

        try
        {
            var media = await _server.GetMediaAsync(meetingId, cancellationToken);
            var preview = media.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.PreviewStorageKey));
            if (preview is not null && Guid.TryParse(preview.Id, out var mediaId))
            {
                var path = await _server.DownloadPreviewAsync(mediaId, cancellationToken);
                if (path is not null)
                {
                    PreviewPlayer.Source = new Uri(path, UriKind.Absolute);
                    PlayerStatusText.Text = $"Файл: {preview.OriginalName}";
                }
                else PlayerStatusText.Text = "Preview недоступен.";
            }
            else PlayerStatusText.Text = "Медиафайл для воспроизведения не найден.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) { PlayerStatusText.Text = $"Плеер: {SafeError(ex)}"; }
        FooterText.Text = "Данные совещания загружены";
    }
    private void TranscriptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranscriptList.SelectedItem is not DesktopTranscriptSegment segment) return;
        if (PreviewPlayer.NaturalDuration.HasTimeSpan)
            PreviewPlayer.Position = TimeSpan.FromMilliseconds(segment.StartMs);
        PreviewPlayer.Play();
        PlayerStatusText.Text = $"Переход к {segment.TimeLabel} · {segment.Speaker ?? "Спикер N"}";
    }

    private void PreviewPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e) => PlayerStatusText.Text = "Не удалось воспроизвести preview. Проверьте кодеки и файл.";

    private void PlayButton_Click(object sender, RoutedEventArgs e) => PreviewPlayer.Play();
    private void PlayerPauseButton_Click(object sender, RoutedEventArgs e) => PreviewPlayer.Pause();
    private void PlayerStopButton_Click(object sender, RoutedEventArgs e) => PreviewPlayer.Stop();

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || !PreviewPlayer.NaturalDuration.HasTimeSpan) return;
        PreviewPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void UpdatePlayerPosition()
    {
        if (!PreviewPlayer.NaturalDuration.HasTimeSpan) return;
        _updatingSlider = true;
        PositionSlider.Maximum = PreviewPlayer.NaturalDuration.TimeSpan.TotalSeconds;
        PositionSlider.Value = Math.Min(PreviewPlayer.Position.TotalSeconds, PositionSlider.Maximum);
        _updatingSlider = false;
    }

    private void SpeakerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeakerList.SelectedItem is DesktopSpeaker speaker) SpeakerNameBox.Text = speaker.DisplayName;
    }

    private async void RenameSpeakerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMeeting is null || SpeakerList.SelectedItem is not DesktopSpeaker speaker || string.IsNullOrWhiteSpace(SpeakerNameBox.Text))
        { FooterText.Text = "Выберите спикера и укажите имя."; return; }
        if (!Guid.TryParse(_selectedMeeting.Id, out var meetingId) || !Guid.TryParse(speaker.Id, out var speakerId)) return;
        try
        {
            if (!await _server.RenameSpeakerAsync(meetingId, speakerId, SpeakerNameBox.Text.Trim())) throw new InvalidOperationException("Сервер отклонил переименование.");
            await LoadMeetingDetailsAsync(_selectedMeeting);
            FooterText.Text = "Имя спикера сохранено.";
        }
        catch (Exception ex) { FooterText.Text = $"Переименование: {SafeError(ex)}"; }
    }

    private async void MergeSpeakersButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMeeting is null || MergeSourceBox.SelectedItem is not DesktopSpeaker source || MergeTargetBox.SelectedItem is not DesktopSpeaker target || source.Id == target.Id)
        { FooterText.Text = "Выберите разных исходного и целевого спикеров."; return; }
        if (!Guid.TryParse(_selectedMeeting.Id, out var meetingId) || !Guid.TryParse(source.Id, out var sourceId) || !Guid.TryParse(target.Id, out var targetId)) return;
        try
        {
            if (!await _server.MergeSpeakersAsync(meetingId, sourceId, targetId)) throw new InvalidOperationException("Сервер отклонил объединение.");
            await LoadMeetingDetailsAsync(_selectedMeeting);
            FooterText.Text = "Спикеры объединены.";
        }
        catch (Exception ex) { FooterText.Text = $"Объединение: {SafeError(ex)}"; }
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskList.SelectedItem is not DesktopTask task) return;
        _selectedTask = task;
        TaskEditBox.Text = task.Task;
        TaskResponsibleBox.Text = task.Responsible ?? string.Empty;
        TaskDeadlineBox.Text = task.Deadline?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;
        TaskStatusBox.SelectedItem = TaskStatusBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Content?.ToString(), task.Status, StringComparison.OrdinalIgnoreCase));
    }

    private async void SaveTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTask is null || string.IsNullOrWhiteSpace(TaskEditBox.Text)) { FooterText.Text = "Выберите поручение и заполните текст."; return; }
        var status = TaskStatusBox.SelectedItem is ComboBoxItem item ? item.Content?.ToString() ?? _selectedTask.Status : _selectedTask.Status;
        DateTime? deadline = null;
        if (!string.IsNullOrWhiteSpace(TaskDeadlineBox.Text))
        {
            if (!DateTime.TryParseExact(TaskDeadlineBox.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDeadline))
            { FooterText.Text = "Срок должен быть в формате ГГГГ-ММ-ДД ЧЧ:ММ."; return; }
            deadline = DateTime.SpecifyKind(parsedDeadline, DateTimeKind.Local).ToUniversalTime();
        }
        try
        {
            var updated = _selectedTask with { Task = TaskEditBox.Text.Trim(), Responsible = string.IsNullOrWhiteSpace(TaskResponsibleBox.Text) ? null : TaskResponsibleBox.Text.Trim(), Deadline = deadline, Status = status };
            if (!await _server.UpdateTaskAsync(updated)) throw new InvalidOperationException("Сервер отклонил изменение поручения.");
            if (_selectedMeeting is not null) await LoadMeetingDetailsAsync(_selectedMeeting);
            FooterText.Text = "Поручение сохранено.";
        }
        catch (Exception ex) { FooterText.Text = $"Сохранение поручения: {SafeError(ex)}"; }
    }

    private async void RebuildSummaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMeeting is null || !Guid.TryParse(_selectedMeeting.Id, out var meetingId)) return;
        var accepted = await _server.RebuildSummaryAsync(meetingId);
        FooterText.Text = accepted ? "Запрос на пересборку саммари принят" : "Не удалось запустить пересборку саммари";
        if (accepted) _ = TrackProcessingAsync(meetingId);
    }

    private static string FormatSummary(DesktopSummary? summary)
    {
        if (summary is null) return "Саммари пока не готово.";
        var root = summary.Content.RootElement;
        if (root.TryGetProperty("summary", out var text) && text.ValueKind == JsonValueKind.String)
            return $"Версия {summary.Version}; модель: {summary.ModelName}\n\n{text.GetString()}";
        return $"Версия {summary.Version}; модель: {summary.ModelName}\n\n{root}";
    }

    private static string SafeError(Exception ex)
    {
        if (ex is HttpRequestException or IOException or TimeoutException)
            return "Локальный сервис недоступен или не ответил вовремя.";
        if (ex is UnauthorizedAccessException)
            return "Недостаточно прав для выполнения операции.";
        if (ex is InvalidOperationException && ex.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
            return "Recorder Service ещё не настроен.";
        return "Операция не выполнена. Откройте вкладку «Администрирование» для диагностики.";
    }

    private async Task TrackProcessingAsync(Guid meetingId)
    {
        _processingPollCts?.Cancel();
        _processingPollCts?.Dispose();
        _processingPollCts = new CancellationTokenSource(TimeSpan.FromHours(4));
        var cancellationToken = _processingPollCts.Token;
        ProcessingProgressBar.Visibility = Visibility.Visible;
        ProcessingProgressBar.Value = 0;
        ProcessingErrorText.Text = string.Empty;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var jobs = await _server.GetJobsAsync(meetingId, cancellationToken);
                var active = jobs.FirstOrDefault(job => !IsTerminal(job.Status));
                if (active is not null)
                {
                    ProcessingProgressBar.Value = Math.Clamp(active.Progress, 0, 100);
                    ProcessingStatusText.Text = $"Обработка: {StageLabel(active.Stage)} · {active.Progress}%";
                    if (active.Status == "FAILED")
                    {
                        ProcessingErrorText.Text = "Обработка завершилась с ошибкой. Стенограмму можно использовать, а саммари — повторить.";
                        break;
                    }
                }
                else
                {
                    active = jobs.FirstOrDefault();
                }

                var summaryReady = active is null && jobs.Any(job => job.Type == "SUMMARIZE" && job.Status == "READY");
                var transcriptReady = jobs.Any(job => job.Type == "TRANSCRIBE" && job.Status == "READY");
                if (summaryReady)
                {
                    ProcessingProgressBar.Value = 100;
                    ProcessingStatusText.Text = "Готово: стенограмма и саммари сохранены";
                    ProcessingErrorText.Text = string.Empty;
                    break;
                }
                if (active is null && transcriptReady && jobs.All(job => job.Type != "SUMMARIZE" || IsTerminal(job.Status)))
                {
                    ProcessingProgressBar.Value = 100;
                    ProcessingStatusText.Text = "Стенограмма готова; саммари можно пересобрать";
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            var meetings = await _server.GetMeetingsAsync(cancellationToken);
            MeetingsList.ItemsSource = meetings;
            var selected = meetings.FirstOrDefault(item => item.Id.Equals(meetingId.ToString(), StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                MeetingsList.SelectedItem = selected;
                _selectedMeeting = selected;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ProcessingErrorText.Text = SafeError(ex);
            ProcessingStatusText.Text = "Не удалось получить статус обработки";
        }
        finally
        {
            ProcessingProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private static bool IsTerminal(string status) => status is "READY" or "FAILED" or "CANCELLED";

    private static string StageLabel(string stage) => stage switch
    {
        "INGEST" or "INGESTING" => "подготовка медиа",
        "NORMALIZING" => "нормализация аудио",
        "TRANSCRIBING" => "транскрибация",
        "ALIGNING" => "выравнивание слов",
        "DIARIZING" => "диаризация",
        "QUALITY_CHECK" => "проверка качества",
        "TRANSCRIPT_READY" => "стенограмма сохранена",
        "SUMMARIZING" => "формирование саммари",
        "READY" => "готово",
        "FAILED" => "ошибка",
        _ => stage,
    };

    private static string FormatBytes(long value)
    {
        if (value <= 0) return "неизвестно";
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var size = (double)value;
        var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size:0.0} {units[index]}";
    }
}
