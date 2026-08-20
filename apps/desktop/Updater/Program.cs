using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

var arguments = ParseArgs(args);
if (!arguments.TryGetValue("desktop-pid", out var pidText)
    || !int.TryParse(pidText, out var desktopPid)
    || !arguments.TryGetValue("installer", out var installer)
    || !File.Exists(installer)
    || !arguments.TryGetValue("expected-identity", out var expectedIdentity)
    || string.IsNullOrWhiteSpace(expectedIdentity)
    || expectedIdentity.Contains("dev", StringComparison.OrdinalIgnoreCase)
    || expectedIdentity.Contains("dirty", StringComparison.OrdinalIgnoreCase))
{
    return Fail("UPDATER_ARGUMENTS_INVALID");
}
if (!arguments.TryGetValue("expected-sha256", out var expectedSha256)
    || expectedSha256.Length != 64
    || !expectedSha256.All(Uri.IsHexDigit)
    || !arguments.TryGetValue("expected-size", out var expectedSizeText)
    || !long.TryParse(expectedSizeText, out var expectedSize)
    || expectedSize <= 0)
{
    return Fail("UPDATER_PACKAGE_METADATA_INVALID");
}
expectedSha256 = expectedSha256.ToLowerInvariant();
if (!await ValidatePackageAsync(installer, expectedSha256, expectedSize))
    return Fail("UPDATER_PACKAGE_VALIDATION_FAILED");

var appRoot = AppContext.BaseDirectory;
var stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "Updates");
Directory.CreateDirectory(stateRoot);
var statePath = Path.Combine(stateRoot, "updater-state.json");
var installRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
if (string.IsNullOrWhiteSpace(installRoot)) return Fail("UPDATER_INSTALL_ROOT_INVALID");
var rollbackPath = Path.Combine(stateRoot, "rollback", DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));
var rollbackSnapshot = Path.Combine(rollbackPath, "install");
await WriteStateAsync(statePath, "WAITING_DESKTOP", expectedIdentity, null);

try
{
    try
    {
        using var desktop = Process.GetProcessById(desktopPid);
        if (!desktop.HasExited) await desktop.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }
    catch (ArgumentException) { }
    catch (TimeoutException) { return Fail("UPDATER_DESKTOP_DID_NOT_EXIT", statePath, expectedIdentity); }

    try { CopyDirectory(installRoot, rollbackSnapshot, Path.Combine(appRoot, "WhisperX.Atom.Updater.exe")); }
    catch (Exception ex) { return Fail("UPDATER_ROLLBACK_SNAPSHOT_FAILED", statePath, expectedIdentity, ex.Message); }
    await WriteStateAsync(statePath, "INSTALLING", expectedIdentity, null);
    var setup = new ProcessStartInfo(installer)
    {
        UseShellExecute = true,
        Verb = "runas",
        WorkingDirectory = Path.GetDirectoryName(installer) ?? appRoot
    };
    setup.ArgumentList.Add("/SILENT");
    setup.ArgumentList.Add("/NORESTART");
    using var process = Process.Start(setup) ?? throw new InvalidOperationException("INSTALLER_START_FAILED");
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        var restored = TryRestoreDirectory(rollbackSnapshot, installRoot, Path.Combine(appRoot, "WhisperX.Atom.Updater.exe"));
        return Fail("UPDATER_INSTALLER_FAILED", statePath, expectedIdentity, restored ? "previous binaries restored" : "rollback failed");
    }

    var desktopPath = Path.Combine(appRoot, "WhisperX.Atom.Desktop.exe");
    if (!VerifyInstalledIdentity(installRoot, expectedIdentity))
    {
        var restored = TryRestoreDirectory(rollbackSnapshot, installRoot, Path.Combine(appRoot, "WhisperX.Atom.Updater.exe"));
        return Fail("UPDATER_POST_INSTALL_IDENTITY_MISMATCH", statePath, expectedIdentity, restored ? "previous binaries restored" : "rollback failed");
    }
    PruneRollbackSnapshots(Path.Combine(stateRoot, "rollback"));
    await WriteStateAsync(statePath, "INSTALLED", expectedIdentity, null);
    if (File.Exists(desktopPath)) Process.Start(new ProcessStartInfo(desktopPath) { UseShellExecute = true, WorkingDirectory = appRoot });
    return 0;
}
catch (Exception ex)
{
    return Fail("UPDATER_FAILED", statePath, expectedIdentity, ex.Message);
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        var key = args[i].TrimStart('-');
        if (!string.IsNullOrWhiteSpace(key)) result[key] = args[i + 1];
    }
    return result;
}

static async Task WriteStateAsync(string path, string state, string identity, string? error)
{
    var part = path + ".part";
    await File.WriteAllTextAsync(part, JsonSerializer.Serialize(new { state, expectedIdentity = identity, error, updatedAtUtc = DateTimeOffset.UtcNow }));
    File.Move(part, path, true);
}

static async Task<bool> ValidatePackageAsync(string path, string expectedSha256, long expectedSize)
{
    try
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedSize) return false;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        return actual == expectedSha256;
    }
    catch { return false; }
}

static bool VerifyInstalledIdentity(string installRoot, string expectedIdentity)
{
    try
    {
        var path = Path.Combine(installRoot, "Desktop", "build-identity.json");
        if (!File.Exists(path)) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("buildIdentity", out var identity)
            || !string.Equals(identity.GetString(), expectedIdentity, StringComparison.Ordinal)
            || !root.TryGetProperty("components", out var components)
            || components.ValueKind != JsonValueKind.Array) return false;
        var rootPrefix = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var component in components.EnumerateArray())
        {
            if (!component.TryGetProperty("path", out var relativeElement)
                || !component.TryGetProperty("sha256", out var hashElement)) return false;
            var relative = relativeElement.GetString();
            var expectedHash = hashElement.GetString();
            if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(expectedHash)) return false;
            var file = Path.GetFullPath(Path.Combine(installRoot, relative));
            if (!file.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return false;
            using var stream = File.OpenRead(file);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    catch { return false; }
}

static void CopyDirectory(string source, string destination, string excludedFile)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source))
    {
        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(excludedFile), StringComparison.OrdinalIgnoreCase)) continue;
        var target = Path.Combine(destination, Path.GetFileName(file));
        File.Copy(file, target, true);
    }
    foreach (var directory in Directory.EnumerateDirectories(source))
        CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), excludedFile);
}

static bool TryRestoreDirectory(string snapshot, string destination, string excludedFile)
{
    try
    {
        // A copy-only restore is not a rollback: files introduced by the
        // failed package would survive and create a mixed runtime. Remove
        // only managed files/directories absent from the snapshot; user data
        // lives outside this Program Files install root.
        RemoveFilesNotInSnapshot(snapshot, destination, excludedFile);
        CopyDirectory(snapshot, destination, excludedFile);
        return true;
    }
    catch { return false; }
}

static void RemoveFilesNotInSnapshot(string snapshot, string destination, string excludedFile)
{
    var snapshotRoot = Path.GetFullPath(snapshot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var excluded = Path.GetFullPath(excludedFile);
    if (!Directory.Exists(destinationRoot)) return;

    foreach (var file in Directory.EnumerateFiles(destinationRoot, "*", SearchOption.AllDirectories).ToArray())
    {
        if (string.Equals(Path.GetFullPath(file), excluded, StringComparison.OrdinalIgnoreCase)) continue;
        var relative = Path.GetRelativePath(destinationRoot, file);
        var snapshotFile = Path.Combine(snapshotRoot, relative);
        if (!File.Exists(snapshotFile)) File.Delete(file);
    }

    foreach (var directory in Directory.EnumerateDirectories(destinationRoot, "*", SearchOption.AllDirectories)
                 .OrderByDescending(path => path.Length).ToArray())
    {
        var relative = Path.GetRelativePath(destinationRoot, directory);
        var snapshotDirectory = Path.Combine(snapshotRoot, relative);
        if (Directory.Exists(snapshotDirectory)) continue;
        // The updater is running from this root and its binary is locked until
        // exit, so preserve only the directory containing that excluded file.
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var containsExcluded = string.Equals(Path.GetFullPath(directory), Path.GetDirectoryName(excluded), StringComparison.OrdinalIgnoreCase)
            || excluded.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
        if (!containsExcluded && Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

static void PruneRollbackSnapshots(string root)
{
    try
    {
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var directory in Directory.EnumerateDirectories(root))
            if (Directory.GetCreationTimeUtc(directory) < cutoff) Directory.Delete(directory, true);
    }
    catch { /* cleanup is best effort and never affects a successful update */ }
}

static int Fail(string code, string? statePath = null, string? identity = null, string? detail = null)
{
    if (statePath is not null && identity is not null)
        WriteStateAsync(statePath, "FAILED", identity, code + (detail is null ? string.Empty : ": " + detail)).GetAwaiter().GetResult();
    Console.Error.WriteLine(code);
    return 1;
}
