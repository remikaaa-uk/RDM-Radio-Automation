using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace RDM.UI.Services;

/// <summary>
/// Writes all ILogger output to a rolling text file next to the executable.
/// Thread-safe via lock. Floor: RDM.* categories = Trace, everything else = Debug
/// (debugging phase — capture as much as possible in rdm.log).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _lock = new();

    public FileLoggerProvider(string path)
    {
        _path = path;
        // Write session header so restarts are clearly separated in the log.
        try
        {
            File.AppendAllText(_path,
                $"\n{'=',60}\n" +
                $"  RDM session started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"{'=',60}\n");
        }
        catch { /* ignore — log file inaccessible */ }
    }

    public ILogger CreateLogger(string categoryName)
        => new FileLogger(_path, categoryName, _lock);

    public void Dispose() { }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _path;
    private readonly string _category;
    private readonly object _lock;

    // Show only the last segment of the namespace (e.g. "PlaylistEngine" not "RDM.Core.Services.PlaylistEngine").
    private readonly string _shortCategory;

    public FileLogger(string path, string category, object @lock)
    {
        _path     = path;
        _category = category;
        _lock     = @lock;
        var parts = category.Split('.');
        _shortCategory = parts.Length > 1 ? $"{parts[^2]}.{parts[^1]}" : category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
        => logLevel >= (_category.StartsWith("RDM.", StringComparison.Ordinal) ? LogLevel.Trace : LogLevel.Debug);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var level = logLevel switch
        {
            LogLevel.Trace       => "TRACE",
            LogLevel.Debug       => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning     => "WARN ",
            LogLevel.Error       => "ERROR",
            LogLevel.Critical    => "CRIT ",
            _                    => "     "
        };

        var message = formatter(state, exception);
        var line    = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {_shortCategory}: {message}";

        if (exception is not null)
            line += $"\n           {exception.GetType().Name}: {exception.Message}\n           {exception.StackTrace?.Split('\n')[0].Trim()}";

        try
        {
            lock (_lock)
                File.AppendAllText(_path, line + "\n");
        }
        catch { /* ignore write errors */ }
    }
}
