using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace WhisperX_Atom_Desktop.Controls;

public sealed partial class AudioWaveformMonitor : UserControl
{
    public AudioWaveformMonitor() => InitializeComponent();

    public IReadOnlyList<double>? Samples
    {
        get => (IReadOnlyList<double>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<double>), typeof(AudioWaveformMonitor), new PropertyMetadata(null, OnWaveformChanged));

    public bool IsStale
    {
        get => (bool)GetValue(IsStaleProperty);
        set => SetValue(IsStaleProperty, value);
    }

    public static readonly DependencyProperty IsStaleProperty = DependencyProperty.Register(
        nameof(IsStale), typeof(bool), typeof(AudioWaveformMonitor), new PropertyMetadata(true, OnWaveformChanged));

    public string SignalState
    {
        get => (string)GetValue(SignalStateProperty);
        set => SetValue(SignalStateProperty, value);
    }

    public static readonly DependencyProperty SignalStateProperty = DependencyProperty.Register(
        nameof(SignalState), typeof(string), typeof(AudioWaveformMonitor), new PropertyMetadata("UNKNOWN", OnWaveformChanged));

    private static void OnWaveformChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is AudioWaveformMonitor monitor) monitor.RenderWaveform();
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderWaveform();

    private void RenderWaveform()
    {
        if (WaveformCanvas is null || WaveformCanvas.ActualWidth <= 0 || WaveformCanvas.ActualHeight <= 0) return;
        var samples = Samples;
        WaveformCanvas.Children.Clear();
        const int barCount = 24;
        var width = Math.Max(1, WaveformCanvas.ActualWidth - 8);
        var height = Math.Max(1, WaveformCanvas.ActualHeight - 8);
        var gap = 3d;
        var barWidth = Math.Max(2d, (width - gap * (barCount - 1)) / barCount);
        var brush = ResolveBrush(IsStale ? "MutedTextBrush" : SignalState switch
        {
            "CLIPPING" => "DangerBrush",
            "READY_NO_SIGNAL" or "NO_PACKETS" => "WarningBrush",
            "READY" => "SuccessBrush",
            _ => "AccentBrush"
        });
        for (var index = 0; index < barCount; index++)
        {
            var sampleIndex = samples is { Count: > 0 }
                ? Math.Min(samples.Count - 1, (int)Math.Round(index * (samples.Count - 1d) / (barCount - 1)))
                : -1;
            var value = sampleIndex >= 0 ? Math.Clamp(samples![sampleIndex], 0d, 1d) : 0d;
            var barHeight = Math.Clamp(6d + value * (height - 6d), 6d, height);
            var bar = new Rectangle { Width = barWidth, Height = barHeight, Fill = brush, RadiusX = 2, RadiusY = 2, Opacity = IsStale ? 0.45 : 0.95 };
            Canvas.SetLeft(bar, 4d + index * (barWidth + gap));
            Canvas.SetTop(bar, 4d + (height - barHeight) / 2d);
            WaveformCanvas.Children.Add(bar);
        }
    }

    private static Brush ResolveBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
}
