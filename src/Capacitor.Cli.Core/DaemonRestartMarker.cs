using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

/// <summary>
/// The <c>&lt;name&gt;.restart-pending</c> marker the daemon writes when a
/// restart-after-update is queued and clears on (successor) startup. Read by
/// <c>kcap daemon status</c> for observability — same on-disk pattern as the PID
/// file, so status needs no socket round-trip. JSON via <see cref="JsonObject"/>
/// to stay NativeAOT-safe (no reflection serializer).
/// </summary>
public sealed record DaemonRestartMarker(string RunningVersion, string Reason, DateTimeOffset QueuedAt) {
    public static void Write(DaemonStore store, string daemonName, DaemonRestartMarker m) {
        store.EnsureDirectory();
        var obj = new JsonObject {
            ["running_version"] = m.RunningVersion,
            ["reason"]          = m.Reason,
            ["queued_at"]       = m.QueuedAt,
        };
        var path = store.RestartPendingPath(daemonName);
        var tmp  = $"{path}.tmp";
        File.WriteAllText(tmp, obj.ToJsonString());
        File.Move(tmp, path, overwrite: true);
    }

    public static DaemonRestartMarker? TryRead(DaemonStore store, string daemonName) {
        var path = store.RestartPendingPath(daemonName);
        if (!File.Exists(path)) return null;
        try {
            var node = JsonNode.Parse(File.ReadAllText(path));
            var ver  = node?["running_version"]?.GetValue<string>();
            var why  = node?["reason"]?.GetValue<string>() ?? "requested";
            var when = node?["queued_at"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UnixEpoch;
            return ver is null ? null : new DaemonRestartMarker(ver, why, when);
        } catch {
            return null; // corrupt marker — treat as absent
        }
    }

    public static void Delete(DaemonStore store, string daemonName) {
        try { File.Delete(store.RestartPendingPath(daemonName)); } catch { /* best-effort */ }
    }

    /// <summary>One-line status text for <c>kcap daemon status</c>.</summary>
    public string Describe() =>
        $"restart pending: running {RunningVersion}, newer binary detected on disk "
      + $"(queued {QueuedAt.LocalDateTime:yyyy-MM-dd HH:mm}, {Reason})";
}
