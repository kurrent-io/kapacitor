using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon;

/// <summary>
/// A minimal file logger that rotates when the log exceeds <see cref="DefaultMaxSize"/>.
/// Keeps one current file and one rolled-over backup (.1).
/// </summary>
sealed class RollingFileLoggerProvider : ILoggerProvider {
    readonly string _path;
    readonly long   _maxSize;
    readonly object _lock = new();
    StreamWriter?   _writer;

    public const long DefaultMaxSize = 10 * 1024 * 1024; // 10 MB

    public RollingFileLoggerProvider(string path, long maxSize = DefaultMaxSize) {
        _path    = path;
        _maxSize = maxSize;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    void Write(string category, LogLevel level, string message) {
        lock (_lock) {
            if (_writer is null) return;

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {LevelTag(level)} {category}  {message}";
            _writer.WriteLine(line);

            TryRotate();
        }
    }

    void TryRotate() {
        try {
            if (_writer?.BaseStream is FileStream fs && fs.Length > _maxSize) {
                _writer.Dispose();
                _writer = null; // Prevent ObjectDisposedException on next Write()
                var backup = _path + ".1";
                File.Delete(backup);
                File.Move(_path, backup);
                _writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read)) {
                    AutoFlush = true
                };
            }
        } catch (IOException) {
            // Best-effort rotation — if it fails, try to reopen so logging continues
            if (_writer is null) {
                try {
                    _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read)) {
                        AutoFlush = true
                    };
                } catch (IOException) {
                    // Truly unable to log — messages will be dropped until next rotation attempt
                }
            }
        }
    }

    static string LevelTag(LogLevel level) => level switch {
        LogLevel.Trace       => "trce:",
        LogLevel.Debug       => "dbug:",
        LogLevel.Information => "info:",
        LogLevel.Warning     => "warn:",
        LogLevel.Error       => "fail:",
        LogLevel.Critical    => "crit:",
        _                    => "    :"
    };

    public void Dispose() {
        lock (_lock) {
            _writer?.Dispose();
            _writer = null;
        }
    }

    sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += Environment.NewLine + exception;
            provider.Write(category, logLevel, msg);
        }
    }
}
