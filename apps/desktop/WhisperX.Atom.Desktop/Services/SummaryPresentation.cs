namespace WhisperX_Atom_Desktop.Services;

using WhisperX.Atom.Desktop;

/// <summary>
/// Single presentation gate for summaries.  Invalid/failed content must never
/// be rendered as raw JSON in any Desktop surface.
/// </summary>
public static class SummaryPresentation
{
    public static bool IsDisplayable(DesktopSummary? summary)
    {
        if (summary is null) return false;
        if (IsFailed(summary.ContentValidity)
            || IsFailed(summary.GenerationState)
            || IsFailed(summary.Status)) return false;
        return MeetingProtocolParser.Parse(summary.Content).IsValid;
    }

    public static string Format(DesktopSummary? summary)
    {
        if (summary is null) return "Саммари пока не готово.";
        if (!IsDisplayable(summary))
            return "Саммари не показано: результат не прошёл проверку формата. Запустите пересборку после готовности V2.";

        var parsed = MeetingProtocolParser.Parse(summary.Content);
        return parsed.IsValid
            ? parsed.DisplayText
            : "Саммари не показано: формат результата не подтверждён. Запустите пересборку.";
    }

    private static bool IsFailed(string? value) =>
        string.Equals(value, "INVALID", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "FAILED", StringComparison.OrdinalIgnoreCase);
}
