using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WhisperX_Atom_Desktop.Controls;

public sealed partial class OperationalStatusItem : UserControl
{
    public OperationalStatusItem()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateVisual();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(OperationalStatusItem), new PropertyMetadata(string.Empty));

    public string Status { get => (string)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(string), typeof(OperationalStatusItem), new PropertyMetadata(string.Empty));

    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(OperationalStatusItem), new PropertyMetadata(string.Empty));

    public string Severity { get => (string)GetValue(SeverityProperty); set => SetValue(SeverityProperty, value); }
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(nameof(Severity), typeof(string), typeof(OperationalStatusItem), new PropertyMetadata("neutral", OnSeverityChanged));

    private static void OnSeverityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is OperationalStatusItem item) item.UpdateVisual();
    }

    private void UpdateVisual()
    {
        var key = Severity?.ToLowerInvariant() switch
        {
            "success" => ("SuccessBrush", "SurfaceGreenBrush"),
            "warning" => ("WarningBrush", "SurfaceOrangeBrush"),
            "danger" or "error" => ("DangerBrush", "DangerSurfaceBrush"),
            "info" or "processing" => ("AccentBrush", "SurfaceBlueBrush"),
            _ => ("NeutralStatusBrush", "SurfaceBrush")
        };
        if (Application.Current.Resources[key.Item1] is Brush indicator) Indicator.Fill = indicator;
        if (Application.Current.Resources[key.Item2] is Brush background) Root.Background = background;
        if (Application.Current.Resources[key.Item1] is Brush border) Root.BorderBrush = border;
    }
}
