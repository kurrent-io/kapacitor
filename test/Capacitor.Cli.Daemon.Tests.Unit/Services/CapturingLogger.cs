using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

sealed class CapturingLogger<T> : ILogger<T> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel                logLevel) => true;

    public void Log<TState>(
            LogLevel                         level,
            EventId                          eventId,
            TState                           state,
            Exception?                       ex,
            Func<TState, Exception?, string> formatter
        ) {
        lock (Entries) Entries.Add((level, formatter(state, ex)));
    }
}
