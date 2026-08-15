using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Services.Mutation;

/// App-side duplicated view of the daemon's boot-refusal.json marker; same duplication precedent as Capacitor.Cli.Services.BootRefusalReader (the app never references the daemon assembly).
public sealed record BootRefusalEvidence(
    int Schema, string DaemonName, string Token, string? Expectation, string? Resolved,
    int Pid, string? InstanceId, string? AttemptId);

/// For DETACHED starts, the mutation lane is this marker's single consumer — attribution requires the lane's own attempt GUID, never a foreign one.
public static partial class BootRefusalMarker {
    const int CurrentSchema = 1;

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

    /// Attributes only against a verifiable identity (schema, attemptId, daemonName, Token/InstanceId, Pid, Expectation) — any mismatch returns null, marker untouched.
    public static BootRefusalEvidence? TryAttribute(string daemonName, string attemptId, string? requestCanonicalServer) {
        var evidence = TryRead(daemonName);
        if (evidence is null) return null;
        if (evidence.Schema != CurrentSchema) return null;
        if (evidence.AttemptId is null || evidence.AttemptId != attemptId) return null;
        if (evidence.DaemonName != daemonName) return null;
        if (string.IsNullOrEmpty(evidence.Token)) return null;
        if (evidence.Pid <= 0) return null;
        if (string.IsNullOrEmpty(evidence.InstanceId)) return null;
        if (!ServerIdentity.Matches(evidence.Expectation, requestCanonicalServer)) return null;

        try { File.Delete(MarkerPath(daemonName)); } catch { /* best-effort consume, already attributed */ }
        return evidence;
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(BootRefusalEvidence))]
    partial class BootRefusalJsonCtx : JsonSerializerContext;
}
