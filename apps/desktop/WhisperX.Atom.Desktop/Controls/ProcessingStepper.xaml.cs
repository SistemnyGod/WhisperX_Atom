using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;

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

        var values = Steps.Cast<object?>().ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            var text = new TextBlock
            {
                Text = values[index]?.ToString() ?? string.Empty,
                FontSize = 12,
                Foreground = ResolveBrush(index < CurrentIndex ? "SuccessBrush" : index == CurrentIndex ? "AccentBrush" : "MutedTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            AutomationProperties.SetName(text, $"Этап {index + 1}: {text.Text}{(index == CurrentIndex ? ", текущий" : index < CurrentIndex ? ", завершён" : ", ожидает")} ");
            StepsPanel.Children.Add(text);
            if (index < values.Length - 1)
            {
                var separator = new TextBlock { Text = "→", Foreground = ResolveBrush("BorderBrush") };
                AutomationProperties.SetName(separator, "Переход к следующему этапу");
                StepsPanel.Children.Add(separator);
            }
        }
    }

    private static Brush ResolveBrush(string key) => Application.Current.Resources[key] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
