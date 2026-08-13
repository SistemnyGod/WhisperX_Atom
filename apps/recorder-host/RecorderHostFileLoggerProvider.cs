using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace WhisperX.Atom.Recorder.Host;

/// <summary>
/// Small user-session logger for the installed Host. It is intentionally
/// independent from Windows Event Log because a current-user process may not
/// have Event Log write privileges. The logger stores operational state only;
/// token/cookie/password values are redacted before they reach disk.
/// </summary>
internal sealed class RecorderHostFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly bool _enabled;
    private readonly object _gate = new();
    private static readonly Regex SecretPattern = new(
        @"(?i)(token|cookie|password|authorization)\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public RecorderHostFileLoggerProvider(string path)
    {
        _path = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _enabled = true;
        }
        catch
        {
            // A restricted diagnostic environment must not prevent the
            // user-session Host from opening its IPC pipe and capturing audio.
            // Console logging remains active in Program.cs; file logging is
            // simply disabled until the next Host start.
            _enabled = false;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        if (!_enabled) return;
        try
        {
            var safe = SecretPattern.Replace(message, "$1=<redacted>").ReplaceLineEndings(" ");
            if (exception is not null)
                safe = $"{safe} | {exception.GetType().Name}: {SecretPattern.Replace(exception.Message, "$1=<redacted>").ReplaceLineEndings(" ")}";
            var line = $"{DateTimeOffset.UtcNow:O} [{level}] {category}: {safe}{Environment.NewLine}";

            lock (_gate)
            {
                RotateIfNeeded();
                File.AppendAllText(_path, line);
            }
        }
        catch
        {
            // Logging must never terminate capture or pipe availability.
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length < 4 * 1024 * 1024) return;
            var first = _path + ".2";
            var second = _path + ".1";
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(second)) File.Move(second, first);
            File.Move(_path, second);
        }
        catch
        {
            // Best effort rotation; keep writing the current file if possible.
        }
    }

    private sealed class FileLogger(RecorderHostFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Write(category, logLevel, formatter(state, exception), exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
