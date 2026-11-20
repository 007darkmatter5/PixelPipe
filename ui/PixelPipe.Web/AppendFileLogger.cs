using Microsoft.Extensions.Logging;

namespace PixelPipe.Web;

/// <summary>Appends one line per log event. Used when the Windows host has no console.</summary>
public sealed class AppendFileLogger : ILoggerProvider, ILogger
{
    private readonly string _path;
    private readonly object _gate = new();

    public AppendFileLogger(string path)
    {
        _path = path;
    }

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {logLevel}: {formatter(state, exception)}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_gate)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    public void Dispose()
    {
    }
}
