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
/// is logged and swallowed — audit must never fail a launch decision.
internal sealed partial class LaunchConsentDecisionLog(string stateDir, ILogger logger, long maxBytes = 1_048_576) {
    readonly string _path = Path.Combine(stateDir, "consent-decisions.jsonl");
    readonly object _gate = new();

    public void Record(LaunchConsentRecord rec) {
        lock (_gate) {
            try {
                Directory.CreateDirectory(stateDir);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(stateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var line = JsonSerializer.Serialize(rec, LaunchConsentDecisionJsonCtx.Default.LaunchConsentRecord) + "\n";
                var incoming = Encoding.UTF8.GetByteCount(line);
                if (File.Exists(_path) && new FileInfo(_path).Length + incoming > maxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                var existed = File.Exists(_path);
                File.AppendAllText(_path, line);
                if (!existed && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to append consent decision for {AgentId}", rec.AgentId);
            }
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(LaunchConsentRecord))]
    partial class LaunchConsentDecisionJsonCtx : JsonSerializerContext;
}
