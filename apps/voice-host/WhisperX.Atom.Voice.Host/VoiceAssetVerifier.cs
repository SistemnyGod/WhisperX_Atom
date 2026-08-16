using System.Text.Json;

namespace WhisperX.Atom.Voice.Host;

internal sealed record VoiceAssetStatus(bool IntegrityReady, string? ErrorCode);

internal static class VoiceAssetVerifier
{
    public static VoiceAssetStatus Check(string assetsRoot, string modelPath)
    {
        if (!Directory.Exists(modelPath)) return new(false, "VOICE_MODEL_MISSING");
        var lockPath = Path.Combine(assetsRoot, "voice-models.lock.json");
        if (!File.Exists(lockPath)) return new(false, "VOICE_MODEL_LOCK_MISSING");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("archiveSha256", out var hashElement)) return new(false, "VOICE_MODEL_LOCK_INVALID");
            var hash = hashElement.GetString();
            if (hash is null || hash.Length != 64 || hash.Any(value => !Uri.IsHexDigit(value))) return new(false, "VOICE_MODEL_LOCK_INVALID");
            foreach (var required in new[] { "am", "conf", "graph" })
                if (!Directory.Exists(Path.Combine(modelPath, required))) return new(false, "VOICE_MODEL_STRUCTURE_INVALID");
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return new(false, "VOICE_MODEL_LOCK_INVALID");
            foreach (var file in files.EnumerateArray())
            {
                var relative = file.GetProperty("path").GetString();
                var size = file.GetProperty("size").GetInt64();
                if (string.IsNullOrWhiteSpace(relative)) return new(false, "VOICE_MODEL_LOCK_INVALID");
                var full = Path.GetFullPath(Path.Combine(assetsRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(Path.GetFullPath(assetsRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return new(false, "VOICE_MODEL_LOCK_INVALID");
                if (!File.Exists(full) || new FileInfo(full).Length != size) return new(false, "VOICE_MODEL_INTEGRITY_FAILED");
                if (file.TryGetProperty("sha256", out var fileHashElement))
                {
                    var expected = fileHashElement.GetString();
                    using var stream = File.OpenRead(full);
                    var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return new(false, "VOICE_MODEL_INTEGRITY_FAILED");
                }
            }
            return new(true, null);
        }
        catch (Exception) when (File.Exists(lockPath))
        {
            return new(false, "VOICE_MODEL_LOCK_INVALID");
        }
    }
}
