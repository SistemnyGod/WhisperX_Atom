using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WhisperX_Atom_Desktop.Services;

namespace WhisperX_Atom_Desktop.Controls;

public sealed partial class AssistantMessageBubble : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(DesktopAssistantMessage), typeof(AssistantMessageBubble),
        new PropertyMetadata(null, OnMessageChanged));

    public AssistantMessageBubble()
    {
        InitializeComponent();
    }

    public DesktopAssistantMessage? Message
    {
        get => (DesktopAssistantMessage?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    private static void OnMessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((AssistantMessageBubble)sender).RenderMessage((DesktopAssistantMessage?)args.NewValue);
    }

    private void RenderMessage(DesktopAssistantMessage? message)
    {
        if (message is null)
        {
            RootGrid.Visibility = Visibility.Collapsed;
            return;
        }

        RootGrid.Visibility = Visibility.Visible;
        var isUser = message.IsUser;
        RootGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        Bubble.HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Bubble.Background = Brush(isUser ? "InfoSurfaceBrush" : "SurfaceBrush");
        Bubble.BorderBrush = Brush(isUser ? "HeroBorderBrush" : "BorderBrush");
        RoleText.Text = isUser ? "Вы" : "Мифодий";
        RoleText.Foreground = Brush(isUser ? "AccentBrush" : "TextPrimaryBrush");
        ContentText.Text = message.Content;
        ContentText.Foreground = Brush("TextPrimaryBrush");
        var displayStatus = isUser ? string.Empty : DisplayStatus(message.Status, message.ErrorCode, message.ProcessingStage);
        StatusText.Text = displayStatus;
        StatusText.Foreground = Brush(message.Status.Equals("READY", StringComparison.OrdinalIgnoreCase) || message.Status.Equals("ANSWERED", StringComparison.OrdinalIgnoreCase)
            ? "SuccessBrush"
            : message.Status.Equals("NEEDS_REVIEW", StringComparison.OrdinalIgnoreCase) || message.Status.Equals("ANSWERED_WITH_WARNING", StringComparison.OrdinalIgnoreCase)
                ? "WarningBrush"
                : "MutedTextBrush");
        MetaText.Text = BuildMeta(message);
        MetaText.Visibility = string.IsNullOrWhiteSpace(MetaText.Text) ? Visibility.Collapsed : Visibility.Visible;
        MetaText.Foreground = Brush("MutedTextBrush");
    }

    private static string BuildMeta(DesktopAssistantMessage message)
    {
        var parts = new List<string>();
        if (!message.IsUser && message.Evidence.RootElement.ValueKind == JsonValueKind.Array)
        {
            var count = message.Evidence.RootElement.GetArrayLength();
            if (count > 0) parts.Add($"Источников: {count}");
        }
        if (!string.IsNullOrWhiteSpace(message.VoiceAnswer)) parts.Add("Голосовой ответ готов");
        return string.Join(" · ", parts);
    }

    private static string DisplayStatus(string status, string? errorCode, string? processingStage)
    {
        if (string.Equals(errorCode, "ASSISTANT_WAITING_FOR_GPU", StringComparison.OrdinalIgnoreCase)) return "Ждёт освобождения GPU";
        if (string.Equals(errorCode, "ASSISTANT_GPU_BUSY_TIMEOUT", StringComparison.OrdinalIgnoreCase)) return "GPU занят слишком долго";
        if (string.Equals(errorCode, "LOCAL_COMMAND_REQUIRED", StringComparison.OrdinalIgnoreCase)) return "Нужна явная команда";
        if (!string.IsNullOrWhiteSpace(processingStage))
        {
            var stage = processingStage.Trim().ToUpperInvariant() switch
            {
                "WAITING_FOR_GPU" => "Ждёт GPU",
                "LOADING_MODEL" => "Загружает модель",
                "GENERATING" => "Формирует ответ",
                "GROUNDING" => "Проверяет источники",
                "DELIVERING_TTS" => "Готовит голосовой ответ",
                _ => string.Empty
            };
            if (stage.Length > 0) return stage;
        }
        return status.ToUpperInvariant() switch
        {
            "QUEUED" => "В очереди",
            "RUNNING" => "Обрабатывает",
            "READY" or "ANSWERED" => "Готово",
            "NEEDS_REVIEW" or "ANSWERED_WITH_WARNING" => "Нужна проверка",
            "NO_EVIDENCE" => "Нет подтверждения",
            "GROUNDING_REJECTED" => "Отклонено проверкой",
            "LLM_UNAVAILABLE" => "ИИ временно недоступен",
            "FAILED" => "Ошибка",
            _ => string.Empty
        };
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources[key] as Brush
        ?? new SolidColorBrush(Colors.Transparent);
}
