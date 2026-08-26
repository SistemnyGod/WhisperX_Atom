using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.Controls;

public sealed partial class StatusBadge : UserControl
{
    public StatusBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateVisual();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusBadge), new PropertyMetadata(string.Empty));

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(StatusBadge), new PropertyMetadata(string.Empty, OnStatusChanged));

    private static void OnStatusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is StatusBadge badge) badge.UpdateVisual();
    }

    private void UpdateVisual()
    {
        var status = UiStatusMapper.Map(Status);
        var (foreground, background, border) = status.Kind switch
        {
            UiStatusKind.Success => ("SuccessBrush", "SurfaceGreenBrush", "SuccessBrush"),
            UiStatusKind.Processing => ("AccentBrush", "SurfaceBlueBrush", "HeroBorderBrush"),
            UiStatusKind.Warning => ("WarningBrush", "SurfaceOrangeBrush", "WarningBrush"),
            UiStatusKind.Error => ("DangerBrush", "DangerSurfaceBrush", "DangerBorderBrush"),
            _ => ("MutedTextBrush", "SurfaceBrush", "BorderBrush")
        };

        if (Application.Current.Resources[foreground] is Brush foregroundBrush)
        {
            BadgeText.Foreground = foregroundBrush;
            Indicator.Fill = foregroundBrush;
        }
        if (Application.Current.Resources[background] is Brush backgroundBrush) Root.Background = backgroundBrush;
        if (Application.Current.Resources[border] is Brush borderBrush) Root.BorderBrush = borderBrush;
    }
}
