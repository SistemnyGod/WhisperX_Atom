using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WhisperX.Atom.Desktop;

public sealed record DesktopSettings(string ApiUrl, string Username, string? ProtectedSessionCookie)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperXAtom", "desktop-settings.json");

    public static DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new("http://localhost:8080", "admin", null);
            return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath), JsonOptions)
                ?? new("http://localhost:8080", "admin", null);
        }
        catch (IOException) { return new("http://localhost:8080", "admin", null); }
        catch (JsonException) { return new("http://localhost:8080", "admin", null); }
    }

    public static void Save(string apiUrl, string username, string? sessionCookie)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var settings = new DesktopSettings(apiUrl.TrimEnd('/'), username.Trim(),
            string.IsNullOrWhiteSpace(sessionCookie) ? null : Protect(sessionCookie));
        var temporary = FilePath + ".part";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, FilePath, true);
    }

    public string? UnprotectSessionCookie()
    {
        if (string.IsNullOrWhiteSpace(ProtectedSessionCookie)) return null;
        try { return Unprotect(ProtectedSessionCookie); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    private static string Protect(string value)
    {
        var bytes = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string value)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
