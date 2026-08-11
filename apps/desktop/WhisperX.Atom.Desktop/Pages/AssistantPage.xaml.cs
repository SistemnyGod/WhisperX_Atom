using System.ComponentModel;
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
        try { await _viewModel.LoadAsync(_pageCts.Token); ContextBox.SelectedItem = _viewModel.SelectedContext; UpdateState(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось загрузить ИИ-помощника.")); }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _viewModel?.CancelPending();
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = null;
        base.OnNavigatedFrom(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantViewModel.IsLoading) or nameof(AssistantViewModel.IsAsking) or nameof(AssistantViewModel.StatusText) or nameof(AssistantViewModel.ErrorText) or nameof(AssistantViewModel.HasAnswer) or nameof(AssistantViewModel.HasEvidence) or nameof(AssistantViewModel.HasMessages) or nameof(AssistantViewModel.CanAsk))
            UpdateState();
        if (e.PropertyName is nameof(AssistantViewModel.SelectedConversation)) UpdateContextText();
    }

    private async void ConversationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try { await _viewModel.SelectConversationAsync(ConversationsList.SelectedItem as DesktopAssistantConversation, _pageCts.Token); UpdateState(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось открыть чат.")); }
    }

    private void ContextBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SelectedContext = ContextBox.SelectedItem as AssistantContextOption;
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

    private async void AskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try { await _viewModel.AskAsync(_pageCts.Token); UpdateState(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Помощник не выполнил запрос.")); }
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
        ChatEmptyText.Visibility = _viewModel.HasMessages ? Visibility.Collapsed : Visibility.Visible;
        EvidenceList.Visibility = _viewModel.HasEvidence ? Visibility.Visible : Visibility.Collapsed;
        EvidenceEmptyText.Visibility = _viewModel.HasEvidence ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = _viewModel.StatusText;
        UpdateContextText();
    }

    private void UpdateContextText() => ContextText.Text = _viewModel?.SelectedConversation is { } conversation
        ? $"{conversation.ContextLabel} · {conversation.Title}"
        : "Выберите или создайте чат";

    private void AssistantShell_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 1100;
        AssistantShell.ColumnDefinitions[0].Width = compact ? new GridLength(220) : new GridLength(260);
        AssistantShell.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(340);
        EvidenceCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(ChatCard, 1);
    }

    private void ShowError(string message)
    {
        ErrorInfoBar.Severity = InfoBarSeverity.Error;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }
}
