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

    public AssistantPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = e.Parameter as FrontendServices;
        if (_services is null) return;
        _pageCts = new CancellationTokenSource();
        _viewModel = new AssistantViewModel(_services);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        try
        {
            await _viewModel.LoadAsync(_pageCts.Token);
            ContextBox.SelectedItem = _viewModel.SelectedContext;
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Не удалось загрузить контекст помощника.")); }
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
        if (e.PropertyName is nameof(AssistantViewModel.IsLoading) or nameof(AssistantViewModel.IsAsking) or nameof(AssistantViewModel.StatusText) or nameof(AssistantViewModel.ErrorText) or nameof(AssistantViewModel.HasAnswer) or nameof(AssistantViewModel.HasEvidence) or nameof(AssistantViewModel.HasAnswerWithoutEvidence))
            UpdateState();
        if (e.PropertyName is nameof(AssistantViewModel.AnswerText) or nameof(AssistantViewModel.VoiceAnswerText)) UpdateAnswer();
    }

    private void ContextBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SelectedContext = ContextBox.SelectedItem as AssistantContextOption;
        UpdateState();
    }

    private void QuestionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.Question = QuestionBox.Text;
    }

    private async void AskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try { await _viewModel.AskAsync(_pageCts.Token); UpdateState(); UpdateAnswer(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(UiErrorFormatter.Format(ex, "Помощник не выполнил запрос.")); }
    }

    private void OpenEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AssistantEvidenceItem evidence }) return;
        if (string.IsNullOrWhiteSpace(evidence.MeetingId))
        {
            ShowError("У этого источника нет meetingId. Он отображён, но переход к встрече заблокирован.");
            return;
        }
        App.MainWindow.NavigateTo("meetings", new MeetingNavigationTarget(evidence.MeetingId, evidence.SegmentId, evidence.StartMs));
    }

    private void UpdateState()
    {
        if (_viewModel is null) return;
        LoadingRing.IsActive = _viewModel.IsLoading || _viewModel.IsAsking;
        AskButton.IsEnabled = !_viewModel.IsLoading && !_viewModel.IsAsking && _viewModel.Contexts.Count > 0;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.ErrorText);
        ErrorInfoBar.Message = _viewModel.ErrorText;
        EvidenceList.Visibility = _viewModel.HasEvidence ? Visibility.Visible : Visibility.Collapsed;
        EvidenceEmptyText.Visibility = _viewModel.HasEvidence ? Visibility.Collapsed : Visibility.Visible;
        NoEvidenceText.Visibility = _viewModel.HasAnswerWithoutEvidence ? Visibility.Visible : Visibility.Collapsed;
        GlobalHintText.Visibility = _viewModel.IsGlobalAllowed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateAnswer()
    {
        if (_viewModel is null) return;
        AnswerText.Text = string.IsNullOrWhiteSpace(_viewModel.AnswerText) ? "Ответ появится после обработки запроса." : _viewModel.AnswerText;
        VoiceAnswerText.Text = string.IsNullOrWhiteSpace(_viewModel.VoiceAnswerText) ? string.Empty : $"Краткий ответ: {_viewModel.VoiceAnswerText}";
    }

    private void AssistantContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = !ResponsiveLayout.IsWide(e.NewSize.Width);
        AssistantContentGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        AssistantContentGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(360);
        AssistantContentGrid.RowDefinitions[0].Height = compact ? new GridLength(300) : new GridLength(1, GridUnitType.Star);
        AssistantContentGrid.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(AnswerCard, 0);
        Grid.SetRow(AnswerCard, 0);
        Grid.SetColumn(EvidenceCard, compact ? 0 : 1);
        Grid.SetRow(EvidenceCard, compact ? 1 : 0);
        EvidenceCard.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
    {
        ErrorInfoBar.Severity = InfoBarSeverity.Error;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }
}
