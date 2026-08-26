using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class AssistantPage : Page
{
    private FrontendServices? _services;
    private AssistantViewModel? _viewModel;
    private CancellationTokenSource? _pageCts;
    private PageLayoutMode _layoutMode = PageLayoutMode.Wide;
    private bool _showEvidence;
    private bool _showConversationList;
    private string? _lastLayoutKey;
    private DispatcherQueueTimer? _assistantRefreshTimer;
    private int _assistantRefreshInFlight;

    public AssistantPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = e.Parameter as FrontendServices;
        if (_services is null) return;
        _pageCts = new CancellationTokenSource();
        _viewModel = new AssistantViewModel(_services);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _services.AssistantResultAvailable += Services_AssistantResultAvailable;
        _assistantRefreshTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _assistantRefreshTimer.Interval = TimeSpan.FromSeconds(3);
        _assistantRefreshTimer.Tick += AssistantRefreshTimer_Tick;
        _assistantRefreshTimer.Start();
        try { await _viewModel.LoadAsync(_pageCts.Token); ModeBox.SelectedItem = _viewModel.SelectedMode; ContextBox.SelectedItem = _viewModel.SelectedContext; UpdateState(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось загрузить ИИ-помощника.")); }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_services is not null) _services.AssistantResultAvailable -= Services_AssistantResultAvailable;
        if (_assistantRefreshTimer is not null)
        {
            _assistantRefreshTimer.Stop();
            _assistantRefreshTimer.Tick -= AssistantRefreshTimer_Tick;
            _assistantRefreshTimer = null;
        }
        _viewModel?.CancelPending();
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = null;
        base.OnNavigatedFrom(e);
    }

    private async void AssistantRefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var viewModel = _viewModel;
        var pageCts = _pageCts;
        if (viewModel is null || pageCts is null || viewModel.IsAsking || !viewModel.HasPendingMessages)
            return;
        if (Interlocked.Exchange(ref _assistantRefreshInFlight, 1) != 0) return;
        try
        {
            using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(pageCts.Token);
            refreshCts.CancelAfter(TimeSpan.FromSeconds(5));
            await viewModel.RefreshSelectedConversationAsync(refreshCts.Token);
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось обновить состояние ответа.")); }
        finally { Interlocked.Exchange(ref _assistantRefreshInFlight, 0); }
    }

    private void Services_AssistantResultAvailable(Guid queryId, Guid? conversationId)
    {
        var viewModel = _viewModel;
        var pageCts = _pageCts;
        if (viewModel is null || pageCts is null) return;
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (conversationId is not null && string.Equals(viewModel.SelectedConversation?.Id, conversationId.Value.ToString(), StringComparison.OrdinalIgnoreCase))
                    await viewModel.RefreshSelectedConversationAsync(pageCts.Token);
                else
                {
                    await viewModel.LoadConversationsAsync(pageCts.Token);
                    // A voice follow-up may complete in a chat that was not
                    // selected when the Assistant page was opened. Select it
                    // after the list refresh so the answer and evidence are
                    // visible without a manual refresh or navigation.
                    var conversation = conversationId is null
                        ? null
                        : viewModel.Conversations.FirstOrDefault(item => string.Equals(item.Id, conversationId.Value.ToString(), StringComparison.OrdinalIgnoreCase));
                    if (conversation is not null) await viewModel.SelectConversationAsync(conversation, pageCts.Token);
                }
                UpdateState();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось обновить ответ помощника.")); }
        });
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantViewModel.IsLoading) or nameof(AssistantViewModel.IsAsking) or nameof(AssistantViewModel.StatusText) or nameof(AssistantViewModel.ErrorText) or nameof(AssistantViewModel.HasAnswer) or nameof(AssistantViewModel.HasEvidence) or nameof(AssistantViewModel.HasMessages) or nameof(AssistantViewModel.CanAsk))
            UpdateState();
        if (e.PropertyName is nameof(AssistantViewModel.SelectedConversation))
        {
            UpdateContextText();
            _showEvidence = false;
            _showConversationList = false;
            ApplyAssistantLayout(AssistantShell.ActualWidth);
        }
    }

    private async void ConversationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try
        {
            await _viewModel.SelectConversationAsync(ConversationsList.SelectedItem as DesktopAssistantConversation, _pageCts.Token);
            _showEvidence = false;
            _showConversationList = false;
            UpdateState();
            ApplyAssistantLayout(AssistantShell.ActualWidth);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось открыть чат.")); }
    }

    private void ContextBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        var context = ContextBox.SelectedItem as AssistantContextOption;
        _viewModel.SelectedContext = context;
        if (context is not null)
        {
            var mode = _viewModel.Modes.FirstOrDefault(item => item.Value == (context.IsGlobal ? "MEETING_MEMORY" : "CURRENT_MEETING"));
            if (mode is not null && !ReferenceEquals(ModeBox.SelectedItem, mode)) ModeBox.SelectedItem = mode;
        }
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SelectedMode = ModeBox.SelectedItem as AssistantModeOption;
        if (_viewModel is not null) ContextBox.SelectedItem = _viewModel.SelectedContext;
    }

    private void MessagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.SelectedMessage = MessagesList.SelectedItem as DesktopAssistantMessage;
        UpdateState();
    }

    private async void NewChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try { await _viewModel.CreateConversationAsync(_pageCts.Token); UpdateState(); }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось создать чат.")); }
    }

    private async void RebuildMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null || !_viewModel.CanRebuildMemory) return;
        try
        {
            var completed = await _viewModel.RebuildMemoryAsync(_pageCts.Token);
            if (completed) ErrorInfoBar.IsOpen = false;
            else ShowError("Не удалось обновить индекс памяти.");
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось обновить память Мифодия.")); }
    }

    private async void AskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try { await _viewModel.AskAsync(_pageCts.Token); UpdateState(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Помощник не выполнил запрос.")); }
    }

    private async void QuestionSuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null || sender is not Button { Tag: string question }) return;
        try
        {
            // A suggestion is also a useful first action on an empty page:
            // create the chat lazily, put the question into the editor, and
            // leave the final send under the user's control.
            if (_viewModel.SelectedConversation is null)
            {
                await _viewModel.CreateConversationAsync(_pageCts.Token);
            }

            QuestionBox.Text = question;
            QuestionBox.Focus(FocusState.Programmatic);
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось подготовить вопрос.")); }
    }

    private void ConversationMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null || sender is not Button { Tag: DesktopAssistantConversation conversation }) return;
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Переименовать" };
        rename.Click += async (_, _) => await RenameConversationAsync(conversation);
        var archive = new MenuFlyoutItem { Text = "Архивировать" };
        archive.Click += async (_, _) => await ChangeConversationAsync(() => _viewModel.ArchiveConversationAsync(conversation, _pageCts.Token));
        var delete = new MenuFlyoutItem { Text = "Удалить" };
        delete.Click += async (_, _) => await ChangeConversationAsync(() => _viewModel.DeleteConversationAsync(conversation, _pageCts.Token));
        menu.Items.Add(rename);
        menu.Items.Add(archive);
        menu.Items.Add(delete);
        menu.ShowAt((FrameworkElement)sender);
    }

    private async Task RenameConversationAsync(DesktopAssistantConversation conversation)
    {
        if (_viewModel is null || _pageCts is null) return;
        var titleBox = new TextBox { Text = conversation.Title, MaxLength = 120, PlaceholderText = "Название чата" };
        var dialog = new ContentDialog
        {
            Title = "Переименовать чат",
            Content = titleBox,
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await ChangeConversationAsync(() => _viewModel.RenameConversationAsync(conversation, titleBox.Text, _pageCts.Token));
    }

    private async Task ChangeConversationAsync(Func<Task> action)
    {
        try { await action(); UpdateState(); }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось изменить чат.")); }
    }

    private void QuestionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.Question = QuestionBox.Text;
    }

    private void OpenEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AssistantEvidenceItem evidence }) return;
        if (string.IsNullOrWhiteSpace(evidence.MeetingId)) { ShowError("У этого источника нет данных о совещании."); return; }
        App.MainWindow.NavigateTo("meetings", new MeetingNavigationTarget(evidence.MeetingId, evidence.SegmentId, evidence.StartMs));
    }

    private void UpdateState()
    {
        if (_viewModel is null) return;
        LoadingRing.IsActive = _viewModel.IsLoading || _viewModel.IsAsking;
        AskButton.IsEnabled = _viewModel.CanAsk;
        NewChatButton.IsEnabled = !_viewModel.IsLoading && !_viewModel.IsAsking;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.ErrorText);
        ErrorInfoBar.Message = _viewModel.ErrorText;
        ChatEmptyState.Visibility = _viewModel.HasMessages ? Visibility.Collapsed : Visibility.Visible;
        ChatEmptyText.Visibility = Visibility.Visible;
        EvidenceList.Visibility = _viewModel.HasEvidence ? Visibility.Visible : Visibility.Collapsed;
        EvidenceEmptyText.Visibility = _viewModel.HasEvidence ? Visibility.Collapsed : Visibility.Visible;
        ShowEvidenceButton.Visibility = _layoutMode != PageLayoutMode.Wide && _viewModel.HasEvidence ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _viewModel.StatusText;
        UpdateContextText();
    }

    private void UpdateContextText() => ContextText.Text = _viewModel?.SelectedConversation is { } conversation
        ? $"{conversation.ContextLabel} · {conversation.Title}"
        : "Выберите или создайте чат";

    private void AssistantShell_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyAssistantLayout(e.NewSize.Width);
    }

    private void ApplyAssistantLayout(double width)
    {
        if (AssistantShell is null || _viewModel is null) return;
        var nextMode = ResponsiveLayout.GetMode(width);
        var hasConversation = _viewModel.SelectedConversation is not null;
        var layoutKey = $"{nextMode}:{_showEvidence}:{_showConversationList}:{hasConversation}";
        if (string.Equals(_lastLayoutKey, layoutKey, StringComparison.Ordinal)) return;
        _lastLayoutKey = layoutKey;
        _layoutMode = nextMode;
        var wide = _layoutMode == PageLayoutMode.Wide;
        var compact = _layoutMode == PageLayoutMode.Compact;

        AssistantHeaderActions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        ModeBox.Width = compact ? double.NaN : 190;
        ContextBox.Width = compact ? double.NaN : 270;
        ModeBox.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ContextBox.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;

        if (wide)
        {
            AssistantShell.ColumnDefinitions[0].Width = new GridLength(260);
            AssistantShell.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            AssistantShell.ColumnDefinitions[2].Width = new GridLength(340);
            ConversationCard.Visibility = Visibility.Visible;
            ChatCard.Visibility = Visibility.Visible;
            EvidenceCard.Visibility = Visibility.Visible;
        }
        else if (_showEvidence)
        {
            AssistantShell.ColumnDefinitions[0].Width = compact ? new GridLength(0) : new GridLength(220);
            AssistantShell.ColumnDefinitions[1].Width = new GridLength(0);
            AssistantShell.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            ConversationCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ChatCard.Visibility = Visibility.Collapsed;
            EvidenceCard.Visibility = Visibility.Visible;
        }
        else if (compact && hasConversation && !_showConversationList)
        {
            AssistantShell.ColumnDefinitions[0].Width = new GridLength(0);
            AssistantShell.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            AssistantShell.ColumnDefinitions[2].Width = new GridLength(0);
            ConversationCard.Visibility = Visibility.Collapsed;
            ChatCard.Visibility = Visibility.Visible;
            EvidenceCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            AssistantShell.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(220);
            AssistantShell.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            AssistantShell.ColumnDefinitions[2].Width = new GridLength(0);
            ConversationCard.Visibility = Visibility.Visible;
            ChatCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            EvidenceCard.Visibility = Visibility.Collapsed;
        }

        BackToChatsButton.Visibility = compact && hasConversation && !_showEvidence ? Visibility.Visible : Visibility.Collapsed;
        BackFromEvidenceButton.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        ShowEvidenceButton.Visibility = !wide && _viewModel.HasEvidence && !_showEvidence ? Visibility.Visible : Visibility.Collapsed;
        QuestionSuggestions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        QuestionComposer.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        QuestionComposer.ColumnDefinitions[1].Width = compact ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumn(AskButton, compact ? 0 : 1);
        Grid.SetRow(AskButton, compact ? 1 : 0);
        AskButton.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }

    private void BackToChatsButton_Click(object sender, RoutedEventArgs e)
    {
        _showConversationList = true;
        // Clear only the visual selection so choosing the same chat again
        // still raises SelectionChanged. The loaded conversation remains in
        // the view model and no server request is lost.
        ConversationsList.SelectedItem = null;
        ApplyAssistantLayout(AssistantShell.ActualWidth);
    }

    private void ShowEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        _showEvidence = true;
        ApplyAssistantLayout(AssistantShell.ActualWidth);
    }

    private void BackFromEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        _showEvidence = false;
        ApplyAssistantLayout(AssistantShell.ActualWidth);
    }

    private void ShowError(string message)
    {
        ErrorInfoBar.Severity = InfoBarSeverity.Error;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }
}
