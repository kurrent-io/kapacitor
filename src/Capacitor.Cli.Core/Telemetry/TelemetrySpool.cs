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

    public void Clear() {
        try {
            if (File.Exists(path)) File.Delete(path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Best effort. A spool we fail to clear replays duplicates next time, which
            // over-counts slightly — strictly better than failing the user's command.
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
        } catch (Exception e) when (e is System.Text.Json.JsonException or FormatException) {
            return null;   // a torn or hand-edited line is skipped, never fatal
        }
    }
}
