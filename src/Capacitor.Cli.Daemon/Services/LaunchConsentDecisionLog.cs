using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Hoisted decision record for serialization and external consumption (e.g., Task 5).
internal sealed record LaunchConsentRecord(
    string DecidedAt, string AgentId, string? Requester, bool RequesterIsOwner,
    string Kind, string RepoPath, string Vendor, string Outcome, string Source);

/// Append-only JSONL audit of every consent decision (rule-matched and human), rendered by the
/// desktop app as the Activity feed and by `kcap daemon consent log`. Best-effort: an I/O fault
/// is logged and swallowed — audit must never fail a launch decision. On Unix, files are created
/// 0600 from the first byte (UnixCreateMode) to avoid a world-readable window after umask-default
/// creation; the directory is owner-only (0700) to restrict traversal.
internal sealed partial class LaunchConsentDecisionLog(string stateDir, ILogger logger, long maxBytes = 1_048_576) {
    readonly string _path = Path.Combine(stateDir, "consent-decisions.jsonl");
    readonly object _gate = new();
    bool _dirCreated;

    public void Record(LaunchConsentRecord rec) {
        lock (_gate) {
            try {
                // Lazy directory creation + mode setting: only when first needed, not on every Record() call.
                if (!_dirCreated) {
                    Directory.CreateDirectory(stateDir);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(stateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    _dirCreated = true;
                }

                var line = JsonSerializer.Serialize(rec, LaunchConsentDecisionJsonCtx.Default.LaunchConsentRecord) + "\n";
                var incoming = Encoding.UTF8.GetByteCount(line);
                if (File.Exists(_path) && new FileInfo(_path).Length + incoming > maxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);

                // Write via FileStream with UnixCreateMode to avoid world-readable window on first creation.
                // File.Move preserves Unix file modes (atomic rename), so the rotated .1 file inherits the
                // source file's 0600 mode automatically.
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                using (var fs = new FileStream(_path, options)) {
                    fs.Write(Encoding.UTF8.GetBytes(line));
                }
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to append consent decision for {AgentId}", rec.AgentId);
            }
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(LaunchConsentRecord))]
    partial class LaunchConsentDecisionJsonCtx : JsonSerializerContext;
}
