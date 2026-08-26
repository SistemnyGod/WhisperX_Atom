using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhisperX_Atom_Desktop.Controls;

public sealed partial class EmptyState : UserControl
{
    public EmptyState()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateActionVisibility();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty, OnActionChanged));

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(EmptyState), new PropertyMetadata(null));

    public string IconGlyph
    {
        get => (string)GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(
        nameof(IconGlyph), typeof(string), typeof(EmptyState), new PropertyMetadata("\uE8A5", OnIconChanged));

    private static void OnActionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is EmptyState state) state.UpdateActionVisibility();
    }

    private static void OnIconChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is EmptyState state) state.StateIcon.Glyph = state.IconGlyph;
    }

    private void UpdateActionVisibility() => ActionButton.Visibility = string.IsNullOrWhiteSpace(ActionText)
        ? Visibility.Collapsed
        : Visibility.Visible;
}
