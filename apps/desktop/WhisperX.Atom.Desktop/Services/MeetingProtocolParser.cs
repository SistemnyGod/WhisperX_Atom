using System.Text.Json;

namespace WhisperX_Atom_Desktop.Services;

public sealed record MeetingProtocolQuestion(string Topic, string Context, string Decision);
public sealed record MeetingProtocolTask(string Task, string DeadlineText, string? DeadlineIso);

public sealed record MeetingProtocolView(
    bool IsProtocol,
    bool IsValid,
    IReadOnlyList<MeetingProtocolQuestion> Questions,
    IReadOnlyList<MeetingProtocolTask> Tasks,
    string FallbackText,
    string? Error,
    string TechnicalJson)
{
    public string DisplayText
    {
        get
        {
            if (!IsProtocol) return FallbackText;
            if (!IsValid) return Error ?? "Не удалось прочитать протокол встречи.";
            var lines = new List<string> { "Вопросы и решения" };
            lines.AddRange(Questions.Select((item, index) =>
                $"{index + 1}. {item.Topic}\n   Контекст: {item.Context}\n   Решение: {item.Decision}"));
            lines.Add(string.Empty);
            lines.Add("Задачи и сроки");
            lines.AddRange(Tasks.Select((item, index) =>
                $"{index + 1}. {item.Task}\n   Срок: {item.DeadlineText}"));
            return string.Join(Environment.NewLine, lines);
        }
    }
}

public static class MeetingProtocolParser
{
    public const string Profile = "MEETING_PROTOCOL_RU";

    public static MeetingProtocolView Parse(JsonDocument? content)
    {
        if (content is null)
            return new(false, false, [], [], "Саммари пока не готово.", null, "{}");
        var root = content.RootElement;
        var technical = root.GetRawText();
        var profile = root.TryGetProperty("profile", out var profileValue) && profileValue.ValueKind == JsonValueKind.String
            ? profileValue.GetString()
            : null;
        if (!string.Equals(profile, Profile, StringComparison.OrdinalIgnoreCase))
            return new(false, true, [], [], ReadFallback(root), null, technical);

        try
        {
            var questions = ReadQuestions(root);
            var tasks = ReadTasks(root);
            return new(true, true, questions, tasks, string.Empty, null, technical);
        }
        catch (JsonException ex)
        {
            return new(true, false, [], [], string.Empty, $"Повреждённый протокол: {ex.Message}", technical);
        }
        catch (InvalidOperationException ex)
        {
            return new(true, false, [], [], string.Empty, $"Повреждённый протокол: {ex.Message}", technical);
        }
    }

    private static IReadOnlyList<MeetingProtocolQuestion> ReadQuestions(JsonElement root)
    {
        if (!root.TryGetProperty("questions_and_decisions", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new JsonException("questions_and_decisions отсутствует");
        return value.EnumerateArray().Select(item => new MeetingProtocolQuestion(
            RequiredText(item, "topic"),
            RequiredText(item, "context"),
            RequiredText(item, "decision"))).ToArray();
    }

    private static IReadOnlyList<MeetingProtocolTask> ReadTasks(JsonElement root)
    {
        if (!root.TryGetProperty("tasks", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new JsonException("tasks отсутствует");
        return value.EnumerateArray().Select(item => new MeetingProtocolTask(
            RequiredText(item, "task"),
            OptionalText(item, "deadline_text") ?? "Не указан",
            OptionalText(item, "deadline_iso"))).ToArray();
    }

    private static string RequiredText(JsonElement item, string property)
    {
        var text = OptionalText(item, property);
        return string.IsNullOrWhiteSpace(text) ? throw new JsonException($"{property} отсутствует") : text;
    }

    private static string? OptionalText(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException($"{property} имеет неверный тип");
        return value.GetString()?.Trim();
    }

    private static string ReadFallback(JsonElement root)
    {
        if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String)
            return summary.GetString() ?? "Саммари готово, но текст отсутствует.";
        if (root.TryGetProperty("overview", out var overview) && overview.ValueKind == JsonValueKind.String)
            return overview.GetString() ?? "Саммари готово, но текст отсутствует.";
        return "Саммари готово в общем формате.";
    }
}
