using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhisperX_Atom_Desktop.Controls;

public sealed partial class AudioLevelMeter : UserControl
{
    public AudioLevelMeter() => InitializeComponent();

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(AudioLevelMeter), new PropertyMetadata(string.Empty));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(AudioLevelMeter), new PropertyMetadata(0d));

    public string DbText
    {
        get => (string)GetValue(DbTextProperty);
        set => SetValue(DbTextProperty, value);
    }

    public static readonly DependencyProperty DbTextProperty = DependencyProperty.Register(
        nameof(DbText), typeof(string), typeof(AudioLevelMeter), new PropertyMetadata("—"));
}
