using System.Globalization;

namespace WhisperX.Atom.Voice;

/// <summary>
/// Bounded, deterministic normalization for speech recognizer output.
/// This is deliberately not a fuzzy matcher: unknown words are retained and
/// can never be silently converted into a Recorder action.
/// </summary>
public static class VoiceCommandText
{
    public static string Normalize(string? text)
    {
        var cleaned = new string((text ?? string.Empty)
            .Select(character => char.IsPunctuation(character) ? ' ' : character)
            .ToArray());
        return string.Join(' ', cleaned
            .Trim()
            .ToLower(CultureInfo.GetCultureInfo("ru-RU"))
            .Replace('\u0451', '\u0435')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Joins two final recognizer segments without duplicating an exact
    /// repeat or an overlapping suffix/prefix. Partial results are never
    /// passed here by the runtime.
    /// </summary>
    public static string? MergeFinalSegments(string? first, string? second)
    {
        var left = Normalize(first);
        var right = Normalize(second);
        if (left.Length == 0) return right.Length == 0 ? null : right;
        if (right.Length == 0 || string.Equals(left, right, StringComparison.Ordinal)) return left;

        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var maxOverlap = Math.Min(leftTokens.Length, rightTokens.Length);
        for (var overlap = maxOverlap; overlap > 0; overlap--)
        {
            var matches = true;
            for (var i = 0; i < overlap; i++)
            {
                if (!string.Equals(leftTokens[leftTokens.Length - overlap + i], rightTokens[i], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
            {
                var suffix = string.Join(' ', rightTokens.Skip(overlap));
                return suffix.Length == 0 ? left : $"{left} {suffix}";
            }
        }
        return $"{left} {right}";
    }

    /// <summary>
    /// Removes only wake words at the beginning/end and collapses an exact
    /// repeated approved command. It intentionally leaves unknown words in
    /// place so malformed commands can be rejected locally.
    /// </summary>
    public static string NormalizeCommandCandidate(string? text, IEnumerable<string> wakeWords, IEnumerable<string> commands)
    {
        var value = Normalize(text);
        var wakes = wakeWords
            .Select(Normalize)
            .Where(static item => item.Length > 0)
            .OrderByDescending(static item => item.Length)
            .ToArray();

        for (var i = 0; i < 2; i++)
        {
            var changed = false;
            foreach (var wake in wakes)
            {
                if (value == wake)
                {
                    value = string.Empty;
                    changed = true;
                    break;
                }
                if (value.StartsWith(wake + " ", StringComparison.Ordinal))
                {
                    value = value[(wake.Length + 1)..].Trim();
                    changed = true;
                    break;
                }
                if (value.EndsWith(" " + wake, StringComparison.Ordinal))
                {
                    value = value[..^(wake.Length + 1)].Trim();
                    changed = true;
                    break;
                }
            }
            if (!changed) break;
        }

        var normalizedCommands = commands
            .Select(Normalize)
            .Where(static item => item.Length > 0)
            .OrderByDescending(static item => item.Length)
            .ToArray();
        foreach (var command in normalizedCommands)
        {
            if (value == $"{command} {command}")
                return command;
        }
        return value;
    }
}
