using System.Text;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Append-only JSONL audit file: created 0600 from the first byte (UnixCreateMode) under an
/// owner-only directory, rotated once to `.1` at maxBytes. Best-effort — an I/O fault is logged
/// and swallowed, because audit must never fail the decision it records.
internal sealed class OwnerOnlyJsonlLog(string path, ILogger logger, long maxBytes) {
    readonly object _gate = new();
    bool _dirCreated;

    public void Append(string line, string subjectForLog) {
        lock (_gate) {
            try {
                if (!_dirCreated) {
                    var dir = Path.GetDirectoryName(path)!;
                    Directory.CreateDirectory(dir);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    _dirCreated = true;
                }

                var incoming = Encoding.UTF8.GetByteCount(line) + 1;
                if (File.Exists(path) && new FileInfo(path).Length + incoming > maxBytes)
                    File.Move(path, path + ".1", overwrite: true);

                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                using var fs = new FileStream(path, options);
                fs.Write(Encoding.UTF8.GetBytes(line + "\n"));
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to append audit record for {Subject}", subjectForLog);
            }
        }
    }
}
