using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// CLI-side duplicated view of the daemon's <c>{stateDir}/boot-refusal.json</c> marker —
/// written by <c>Capacitor.Cli.Daemon.Services.BootRefusal</c>, NOT referenced here since
/// the CLI does not depend on the daemon project; same duplication precedent as this file's own
/// duplicated <see cref="VerifyExit"/> tokens). Deliberately omits <c>schema</c>/<c>timestamp</c> —
/// the reader has no use for either, and a missing member simply doesn't bind rather than failing
/// to parse a marker whose schema gains fields this reader never needs.
/// </summary>
public sealed record BootRefusalEvidence(
    string DaemonName, string Token, string? Expectation, string? Resolved,
    int Pid, string? InstanceId, string? AttemptId);

/// <summary>
/// Read-side-only access to the daemon's boot-refusal marker, used by <see cref="ServiceVerify"/>'s
/// gated readiness-timeout path to attribute a service-verb timeout to a specific boot
/// refusal the daemon itself observed. The CLI never writes this marker — only reads, verified-clears
/// (so a later read is trustworthy fresh evidence), and best-effort consumes it after attribution.
/// </summary>
public static partial class BootRefusalReader {
    public static string MarkerPath(string daemonName) =>
        Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(daemonName), "boot-refusal.json");

    /// <summary>Absent or corrupt → null. Unlike the daemon's own reader, a corrupt marker is LEFT IN
    /// PLACE — the CLI has no ownership authority to quarantine/rename a file the daemon writes.
    /// Reads via a share-all FileStream (never File.ReadAllText) — the daemon owns this file and may
    /// concurrently rename/rewrite it; a write-denying open would stall that on Windows.</summary>
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

    /// <summary>
    /// VERIFIED delete: attempts to remove whatever sits at the marker path, then re-checks —
    /// returns false if anything (a locked file, a permission-denied file, or a directory that
    /// <see cref="File.Delete(string)"/> cannot remove) is still there. A caller that gets false must
    /// treat any marker later found at this path as untrustworthy — it may be stale residue from a
    /// previous attempt rather than fresh evidence from this one.
    /// </summary>
    public static bool TryClear(string daemonName) {
        var path = MarkerPath(daemonName);
        try { File.Delete(path); } catch { /* verified below, not here */ }
        return !File.Exists(path) && !Directory.Exists(path);
    }

    /// <summary>Best-effort delete after attribution has already been reported — failure here must
    /// never affect an exit code or emit anything further.</summary>
    public static void Consume(string daemonName) {
        try { File.Delete(MarkerPath(daemonName)); } catch { /* best-effort */ }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(BootRefusalEvidence))]
    partial class BootRefusalJsonCtx : JsonSerializerContext;
}
