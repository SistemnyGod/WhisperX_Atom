using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhisperX_Atom_Desktop.Controls;

public sealed partial class PageHeader : UserControl
{
    public PageHeader() => InitializeComponent();

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
}
