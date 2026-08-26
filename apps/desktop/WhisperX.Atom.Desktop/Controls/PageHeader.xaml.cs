using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhisperX_Atom_Desktop.Controls;

public sealed partial class PageHeader : UserControl
{
    public PageHeader()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateLayout(ActualWidth);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));

    public UIElement? Actions
    {
        get => (UIElement?)GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(UIElement), typeof(PageHeader), new PropertyMetadata(null));

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayout(e.NewSize.Width);

    private void UpdateLayout(double width)
    {
        if (RootGrid is null || ActionsPresenter is null || SubtitleText is null) return;
        var compact = width > 0 && width < 680;
        RootGrid.ColumnDefinitions.Clear();
        RootGrid.RowDefinitions.Clear();

        if (compact)
        {
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < 3; index++) RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(TitleText, 0);
            Grid.SetRow(TitleText, 0);
            Grid.SetColumn(ActionsPresenter, 0);
            Grid.SetRow(ActionsPresenter, 1);
            Grid.SetColumn(SubtitleText, 0);
            Grid.SetColumnSpan(SubtitleText, 1);
            Grid.SetRow(SubtitleText, 2);
            SubtitleText.Margin = new Thickness(0, 8, 0, 0);
            if (Actions is StackPanel actions) actions.Orientation = Orientation.Vertical;
            return;
        }

        RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(TitleText, 0);
        Grid.SetRow(TitleText, 0);
        Grid.SetColumn(ActionsPresenter, 1);
        Grid.SetRow(ActionsPresenter, 0);
        Grid.SetColumn(SubtitleText, 0);
        Grid.SetColumnSpan(SubtitleText, 2);
        Grid.SetRow(SubtitleText, 1);
        SubtitleText.Margin = new Thickness(0, 4, 0, 0);
        if (Actions is StackPanel wideActions) wideActions.Orientation = Orientation.Horizontal;
    }
}
