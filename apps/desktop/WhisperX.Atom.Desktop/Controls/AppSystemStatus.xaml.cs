using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WhisperX_Atom_Desktop.Controls;

public sealed partial class AppSystemStatus : UserControl
{
    public event EventHandler? OpenSystemStatusRequested;

    public AppSystemStatus()
    {
        InitializeComponent();
    }

    public void Apply(
        string summary,
        string brushKey,
        string description,
        string recorder,
        string microphone,
        string storage,
        string server,
        string whisper,
        string voice,
        string qwen)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => Apply(summary, brushKey, description, recorder, microphone, storage, server, whisper, voice, qwen));
            return;
        }

        SummaryText.Text = summary;
        ToolTipService.SetToolTip(SummaryText, summary);
        SummaryDescription.Text = description;
        RecorderText.Text = recorder;
        MicrophoneText.Text = microphone;
        StorageText.Text = storage;
        ServerText.Text = server;
        WhisperText.Text = whisper;
        VoiceText.Text = voice;
        QwenText.Text = qwen;

        if (Application.Current.Resources[brushKey] is Brush brush)
        {
            SummaryIndicator.Fill = brush;
        }
    }

    public void SetSummary(string summary, string brushKey)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetSummary(summary, brushKey));
            return;
        }

        SummaryText.Text = summary;
        ToolTipService.SetToolTip(SummaryText, summary);
        if (Application.Current.Resources[brushKey] is Brush brush)
            SummaryIndicator.Fill = brush;
    }

    private void OpenSystemStatusButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSystemStatusRequested?.Invoke(this, EventArgs.Empty);
    }
}
