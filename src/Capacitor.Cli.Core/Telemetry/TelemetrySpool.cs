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
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Best effort — losing spooled telemetry is never worth failing a command.
        }
    }

    public IReadOnlyList<TelemetryEvent> DrainAll() {
        if (!File.Exists(path)) return [];

        try {
            return File.ReadAllLines(path)
                       .Select(Deserialize)
                       .OfType<TelemetryEvent>()
                       .ToList();
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
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
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Best effort — losing spooled telemetry is never worth failing a command.
        }
    }

    void Trim() {
        var lines = File.ReadAllLines(path);
        if (lines.Length <= maxEvents) return;

        File.WriteAllLines(path, lines[^maxEvents..]);
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

            return new TelemetryEvent(name, (JsonObject)props.DeepClone(), DateTimeOffset.Parse(ts));
        } catch (Exception) {
            // Broad catch required by the never-throw constraint: a torn write, truncated file,
            // or hand-edited spool can produce structurally-valid JSON with unexpected types,
            // which JsonNode.GetValue<T>() raises InvalidOperationException for. Any exception
            // escaping to the NativeAOT runtime causes SIGABRT. Graceful degradation is required.
            return null;
        }
    }
}
