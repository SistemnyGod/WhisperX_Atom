using System.Globalization;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Versioned, self-contained name for a closed PCM chunk. The values are
/// written only after the capture stream is closed, so disk recovery does not
/// have to reconstruct the timeline from a fixed chunk duration.
/// </summary>
internal static class RawChunkFileName
{
    public static string Create(int sequence, long startSample, long sampleCount) =>
        $"{sequence:D8}.s{startSample:D20}.n{sampleCount:D20}.pcm";

    public static bool TryParse(string path, out int sequence, out long startSample, out long sampleCount)
    {
        sequence = 0;
        startSample = 0;
        sampleCount = 0;
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence < 0)
            return false;
        if (!parts[1].StartsWith('s') || !parts[2].StartsWith('n')) return false;
        if (!long.TryParse(parts[1][1..], NumberStyles.None, CultureInfo.InvariantCulture, out startSample) || startSample < 0)
            return false;
        if (!long.TryParse(parts[2][1..], NumberStyles.None, CultureInfo.InvariantCulture, out sampleCount) || sampleCount <= 0)
            return false;
        return true;
    }
}
