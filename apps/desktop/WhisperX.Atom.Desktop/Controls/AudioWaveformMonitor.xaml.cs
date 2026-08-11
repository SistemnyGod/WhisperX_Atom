using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    private static void OnWaveformChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is AudioWaveformMonitor monitor) monitor.RenderWaveform();
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderWaveform();

    private void RenderWaveform()
    {
        if (WaveformLine is null || WaveformCanvas.ActualWidth <= 0 || WaveformCanvas.ActualHeight <= 0) return;
        var samples = Samples;
        var points = new PointCollection();
        var width = Math.Max(1, WaveformCanvas.ActualWidth - 8);
        var height = Math.Max(1, WaveformCanvas.ActualHeight - 8);
        var center = height / 2d + 4;
        var count = samples?.Count ?? 0;
        if (count == 0)
        {
            points.Add(new Point(4, center));
            points.Add(new Point(width + 4, center));
        }
        else
        {
            for (var index = 0; index < count; index++)
            {
                var normalized = Math.Clamp(samples![index], 0d, 1d);
                var x = 4d + width * index / Math.Max(1, count - 1);
                var y = center - normalized * (height / 2d - 2d);
                points.Add(new Point(x, y));
            }
        }
        WaveformLine.Points = points;
        WaveformLine.Opacity = IsStale ? 0.35 : 1.0;
    }
}
