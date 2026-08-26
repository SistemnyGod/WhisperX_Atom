using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;

namespace WhisperX_Atom_Desktop.Controls;

public sealed partial class HighlightedTextBlock : UserControl
{
    public HighlightedTextBlock()
    {
        InitializeComponent();
        Loaded += (_, _) => RenderText();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HighlightedTextBlock), new PropertyMetadata(string.Empty, OnTextChanged));

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query), typeof(string), typeof(HighlightedTextBlock), new PropertyMetadata(string.Empty, OnTextChanged));

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is HighlightedTextBlock control) control.RenderText();
    }

    private void RenderText()
    {
        if (ContentBlock is null) return;
        ContentBlock.Blocks.Clear();
        var paragraph = new Paragraph();
        var text = Text ?? string.Empty;
        var query = Query?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            ContentBlock.Blocks.Add(paragraph);
            return;
        }
        if (query.Length == 0)
        {
            paragraph.Inlines.Add(new Run { Text = text });
            ContentBlock.Blocks.Add(paragraph);
            return;
        }

        var cursor = 0;
        while (cursor < text.Length)
        {
            var match = text.IndexOf(query, cursor, StringComparison.CurrentCultureIgnoreCase);
            if (match < 0)
            {
                paragraph.Inlines.Add(new Run { Text = text[cursor..] });
                break;
            }
            if (match > cursor)
                paragraph.Inlines.Add(new Run { Text = text[cursor..match] });
            paragraph.Inlines.Add(new Run
            {
                Text = text.Substring(match, query.Length),
                Foreground = ResolveBrush("AccentBrush"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            cursor = match + query.Length;
        }
        ContentBlock.Blocks.Add(paragraph);
        AutomationProperties.SetName(ContentBlock, text);
    }

    private static Brush ResolveBrush(string key) =>
        Application.Current.Resources[key] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
}
