using System.Globalization;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Failure fallback for the in-memory queue: events that could not be delivered land here and
/// are replayed by the next successful flush from any kcap process. Bounded and drop-oldest, so
/// a permanently offline machine can never grow the file without limit.
/// </summary>
public sealed class TelemetrySpool(string path, int maxEvents = 2000) {
    public void Append(IReadOnlyList<TelemetryEvent> events) {
        if (events.Count == 0) return;

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = events.Select(Serialize).ToList();
            File.AppendAllLines(path, lines);
            Trim();
        } catch {
            // Telemetry code must NEVER throw. Path validation and other rare exceptions
            // have escaped enumerated filters twice already in this namespace. The never-throw
            // constraint is absolute — losing spooled telemetry is never worth failing a command.
        }
    }

    public IReadOnlyList<TelemetryEvent> DrainAll() {
        try {
            if (!File.Exists(path)) return [];

            return File.ReadAllLines(path)
                       .Select(Deserialize)
                       .OfType<TelemetryEvent>()
                       .ToList();
        } catch {
            // Telemetry code must NEVER throw. Path-validation exceptions like PathTooLongException,
            // NotSupportedException, or ArgumentException are theoretically reachable from a
            // pathological KCAP_CONFIG_DIR. This catch is defence-in-depth; triggering a read or
            // delete failure deterministically across platforms requires filesystem states a unit
            // test can't reliably create (permission-denied files, exclusive locks), so this is
            // not unit-tested. The constraint is absolute: graceful degradation is required.
            return [];
        }
    }

    /// <summary>
    /// Best effort. If Clear fails, the spool replays duplicates on the next drain, which
    /// over-counts slightly — strictly better than failing the user's command. Note: DrainAll
    /// followed by Clear is not atomic. If another kcap process appends between drain and clear,
    /// those events are deleted, not duplicated — an accepted cost of distributed best-effort
    /// telemetry. Concurrent appends during DrainAll can also hit file-sharing IOException,
    /// causing the entire batch to no-op.
    /// </summary>
    public void Clear() {
        try {
            if (File.Exists(path)) File.Delete(path);
        } catch {
            // Telemetry code must NEVER throw. Path-validation exceptions like PathTooLongException,
            // NotSupportedException, or ArgumentException are theoretically reachable from a
            // pathological KCAP_CONFIG_DIR. This catch is defence-in-depth; triggering a delete
            // failure deterministically across platforms requires filesystem states a unit test
            // can't reliably create (exclusive locks), so this is not unit-tested. The constraint
            // is absolute: a failed delete means duplicates on the next drain, not lost events.
        }
    }

    void Trim() {
        try {
            var lines = File.ReadAllLines(path);
            if (lines.Length <= maxEvents) return;

            File.WriteAllLines(path, lines[^maxEvents..]);
        } catch {
            // Telemetry code must NEVER throw. Trim is best-effort; if it fails, the spool
            // grows slightly but the command proceeds. Enumerated filters have missed cases twice
            // already in this namespace — catch broadly.
        }
    }

    static string Serialize(TelemetryEvent e) =>
        new JsonObject {
            ["event"]      = e.Name,
            ["properties"] = e.Properties.DeepClone(),
            ["timestamp"]  = e.Timestamp.ToString("o"),
        }.ToJsonString();

    static TelemetryEvent? Deserialize(string line) {
        try {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            var name = o["event"]?.GetValue<string>();
            var ts   = o["timestamp"]?.GetValue<string>();
            if (name is null || ts is null || o["properties"] is not JsonObject props) return null;

            return new TelemetryEvent(name, (JsonObject)props.DeepClone(), DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None));
        } catch (Exception) {
            // Broad catch required by the never-throw constraint: a torn write, truncated file,
            // or hand-edited spool can produce structurally-valid JSON with unexpected types,
            // which JsonNode.GetValue<T>() raises InvalidOperationException for. Any exception
            // escaping to the NativeAOT runtime causes SIGABRT. Graceful degradation is required.
            return null;
        }
    }
}
