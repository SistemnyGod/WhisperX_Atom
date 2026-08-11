using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;

namespace WhisperX_Atom_Desktop.Pages;

public sealed partial class AgentsPage : Page
{
    private AgentsViewModel? _viewModel;
    private CancellationTokenSource? _pageCts;
    private bool _updatingLayout;

    public AgentsPage()
    {
        InitializeComponent();
        SizeChanged += AgentsPage_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not FrontendServices services) return;
        _pageCts = new CancellationTokenSource();
        _viewModel = new AgentsViewModel(services);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        try
        {
            await _viewModel.RefreshAsync(_pageCts.Token);
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorInfoBar.Message = ex.Message;
            ErrorInfoBar.IsOpen = true;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = null;
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnNavigatedFrom(e);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentsViewModel.SelectedAgent)) UpdateDetails();
        UpdateState();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pageCts is null) return;
        try
        {
            await _viewModel.RefreshAsync(_pageCts.Token);
            AgentsList.SelectedItem = null;
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorInfoBar.Message = ex.Message;
            ErrorInfoBar.IsOpen = true;
        }
    }

    private void AgentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SelectedAgent = AgentsList.SelectedItem as DesktopAgent;
        UpdateDetails();
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("settings");

    private void UpdateState()
    {
        if (_viewModel is null) return;
        LoadingRing.IsActive = _viewModel.IsLoading;
        RefreshButton.IsEnabled = !_viewModel.IsLoading;
        AgentsList.Visibility = _viewModel.HasAgents ? Visibility.Visible : Visibility.Collapsed;
        AgentsEmptyState.Visibility = _viewModel.HasAgents ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = !string.IsNullOrWhiteSpace(_viewModel.ErrorText) ? "Не удалось загрузить реестр" : "Зарегистрированных агентов нет";
        EmptyDescription.Text = !string.IsNullOrWhiteSpace(_viewModel.ErrorText)
            ? _viewModel.ErrorText
            : "Войдите в API или зарегистрируйте Recorder Agent в настройках.";
        ErrorInfoBar.Message = _viewModel.ErrorText;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.ErrorText);
        WarningInfoBar.Message = _viewModel.WarningText;
        WarningInfoBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.WarningText);
        LocalAgentDot.Fill = StatusBrush(_viewModel.LocalAgentAvailable, _viewModel.IsLoading);
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (_viewModel is null) return;
        var agent = _viewModel.SelectedAgent;
        AgentDetails.Visibility = agent is null ? Visibility.Collapsed : Visibility.Visible;
        DetailsEmptyText.Visibility = agent is null ? Visibility.Visible : Visibility.Collapsed;
        if (agent is null) return;
        DetailsName.Text = string.IsNullOrWhiteSpace(agent.Name) ? "Без имени" : agent.Name;
        DetailsStatus.Text = agent.Status;
        DetailsLastSeen.Text = agent.LastSeenText;
        DetailsRoom.Text = agent.RoomText;
    }

    private void AgentsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_updatingLayout) return;
        _updatingLayout = true;
        try
        {
            var mode = ResponsiveLayout.GetMode(e.NewSize.Width);
            ActionsPanel.Orientation = mode == PageLayoutMode.Compact ? Orientation.Vertical : Orientation.Horizontal;
            var compact = mode != PageLayoutMode.Wide;
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(360);
            WorkspaceGrid.RowDefinitions[0].Height = compact ? new GridLength(380) : new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(AgentsCard, 0);
            Grid.SetRow(AgentsCard, 0);
            Grid.SetColumn(DetailsCard, compact ? 0 : 1);
            Grid.SetRow(DetailsCard, compact ? 1 : 0);

            SummaryGrid.ColumnDefinitions.Clear();
            SummaryGrid.RowDefinitions.Clear();
            var cards = SummaryGrid.Children.OfType<FrameworkElement>().ToArray();
            var columns = mode == PageLayoutMode.Compact ? 1 : mode == PageLayoutMode.Standard ? 2 : 3;
            for (var i = 0; i < columns; i++) SummaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < (int)Math.Ceiling(cards.Length / (double)columns); i++) SummaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < cards.Length; i++)
            {
                Grid.SetColumn(cards[i], i % columns);
                Grid.SetRow(cards[i], i / columns);
            }
        }
        finally { _updatingLayout = false; }
    }

    private static Brush StatusBrush(bool available, bool loading)
    {
        var key = loading ? "NeutralStatusBrush" : available ? "SuccessBrush" : "DangerBrush";
        return (Brush)Application.Current.Resources[key];
    }
}
