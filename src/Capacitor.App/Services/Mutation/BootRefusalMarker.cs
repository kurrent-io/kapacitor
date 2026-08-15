using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services.Mutation;

/// App-side duplicated view of the daemon's boot-refusal.json marker; same duplication precedent as Capacitor.Cli.Services.BootRefusalReader (the app never references the daemon assembly).
public sealed record BootRefusalEvidence(
    string DaemonName, string Token, string? Expectation, string? Resolved,
    int Pid, string? InstanceId, string? AttemptId);

/// For DETACHED starts, the mutation lane is this marker's single consumer — attribution requires the lane's own attempt GUID, never a foreign one.
public static partial class BootRefusalMarker {
    public static string MarkerPath(string daemonName) =>
        Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(daemonName), "boot-refusal.json");

    /// Absent or corrupt → null, left in place — the app has no ownership authority over a marker the daemon writes.
    /// Reads via a share-all FileStream (never File.ReadAllText) — the daemon owns this file and may
    /// concurrently rename/rewrite it; a write-denying open would stall that on Windows.
    public static BootRefusalEvidence? TryRead(string daemonName) {
        var path = MarkerPath(daemonName);

        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize(reader.ReadToEnd(), BootRefusalJsonCtx.Default.BootRefusalEvidence);
        } catch {
            // Missing (FileNotFoundException) or corrupt — both answer null, left in place.
            return null;
        }
    }

    /// Attributes only when AttemptId matches attemptId and DaemonName matches daemonName; any mismatch returns null and leaves the marker untouched.
    public static BootRefusalEvidence? TryAttribute(string daemonName, string attemptId) {
        var evidence = TryRead(daemonName);
        if (evidence is null || evidence.AttemptId != attemptId || evidence.DaemonName != daemonName) return null;

        try { File.Delete(MarkerPath(daemonName)); } catch { /* best-effort consume, already attributed */ }
        return evidence;
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(BootRefusalEvidence))]
    partial class BootRefusalJsonCtx : JsonSerializerContext;
}
