using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WhisperX_Atom_Desktop.Controls;

public sealed partial class ProcessingStepper : UserControl
{
    public ProcessingStepper()
    {
        InitializeComponent();
        Loaded += (_, _) => RenderSteps();
    }

    public IEnumerable? Steps
    {
        get => (IEnumerable?)GetValue(StepsProperty);
        set => SetValue(StepsProperty, value);
    }

    public static readonly DependencyProperty StepsProperty = DependencyProperty.Register(
        nameof(Steps), typeof(IEnumerable), typeof(ProcessingStepper), new PropertyMetadata(null, OnStepsChanged));

    public int CurrentIndex
    {
        get => (int)GetValue(CurrentIndexProperty);
        set => SetValue(CurrentIndexProperty, value);
    }

    public static readonly DependencyProperty CurrentIndexProperty = DependencyProperty.Register(
        nameof(CurrentIndex), typeof(int), typeof(ProcessingStepper), new PropertyMetadata(-1, OnStepsChanged));

    private static void OnStepsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is ProcessingStepper stepper) stepper.RenderSteps();
    }

    private void RenderSteps()
    {
        if (StepsPanel is null) return;
        StepsPanel.Children.Clear();
        if (Steps is null) return;

        var index = 0;
        foreach (var item in Steps)
        {
            var text = new TextBlock
            {
                Text = item?.ToString() ?? string.Empty,
                FontSize = 12,
                Foreground = ResolveBrush(index <= CurrentIndex ? "AccentBrush" : "MutedTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            StepsPanel.Children.Add(text);
            index++;
            if (index < Steps.Cast<object?>().Count())
            {
                StepsPanel.Children.Add(new TextBlock { Text = "·", Foreground = ResolveBrush("BorderBrush") });
            }
        }
    }

    private static Brush ResolveBrush(string key) => Application.Current.Resources[key] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
