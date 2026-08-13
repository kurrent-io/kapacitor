using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Contents of the <c>{stateDir}/boot-refusal.json</c> marker a daemon leaves behind when it
/// refuses to start (Task 12, AI-1655): either the server-expectation check
/// (<see cref="DaemonRunner.ExpectationSatisfied"/>) or a Task 11 consent-seed classification
/// (<c>LaunchConsentStore.BootSeed</c>) came back Refused. <see cref="Expectation"/>/
/// <see cref="Resolved"/> mirror <c>config.ExpectedServerUrl</c>/<c>config.ServerUrl</c> at write
/// time regardless of which check actually fired — a consent-seed refusal still carries a
/// non-null <see cref="Expectation"/> whenever the operator configured one (and it was satisfied,
/// which is why the boot got far enough to reach the consent-seed check at all); both are null
/// only when no expectation was configured in the first place. <see cref="Pid"/>/
/// <see cref="InstanceId"/>/<see cref="AttemptId"/> let a caller correlate the marker with the
/// exact boot attempt that wrote it.
/// </summary>
public sealed record BootRefusalRecord(
    int Schema, string DaemonName, string Token, string? Expectation, string? Resolved,
    int Pid, string? InstanceId, string? AttemptId, DateTimeOffset Timestamp);

/// <summary>
/// Owns <c>{stateDir}/boot-refusal.json</c>. Written by <c>DaemonRunner.RunAsync</c>'s pre-host
/// boot-check block right before it returns 0 without ever building the host — so this cannot
/// depend on the logging pipeline, DI, or anything else the host would normally provide. Every
/// entry point is contained: a boot refusal is itself an "unwritable/misconfigured state dir" kind
/// of condition, so the marker writer must never throw on top of it.
/// </summary>
public static partial class BootRefusal {
    const int CurrentSchema = 1;

    public static string MarkerPath(string stateDir) => Path.Combine(stateDir, "boot-refusal.json");

    /// <summary>
    /// Best-effort atomic temp+rename write. NEVER throws — the state dir may be exactly as
    /// unwritable as the condition being reported, and a marker-write failure must not mask (or
    /// replace) the refusal itself. Deliberately does NOT create <paramref name="stateDir"/>
    /// itself — that's the CALLER's responsibility (<c>DaemonRunner.RunAsync</c>'s boot-check
    /// block best-effort-creates it once, up front, precisely so this call has somewhere to land
    /// even on a brand-new daemon name); manufacturing it here would blur "the directory exists"
    /// with "the directory is safe/expected to exist".
    /// </summary>
    public static void TryWrite(string stateDir, DaemonConfig config, string token) {
        try {
            var record = new BootRefusalRecord(
                CurrentSchema, config.Name, token, config.ExpectedServerUrl, config.ServerUrl,
                Environment.ProcessId, config.InstanceId, config.BootAttemptId, DateTimeOffset.UtcNow);

            var path = MarkerPath(stateDir);
            var tmp  = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

            File.WriteAllText(tmp, JsonSerializer.Serialize(record, BootRefusalJsonCtx.Default.BootRefusalRecord));
            File.Move(tmp, path, overwrite: true);
        } catch {
            // Contained: see class doc. Nothing to log to — the host/logging pipeline doesn't
            // exist yet at this point in RunAsync.
        }
    }

    /// <summary>
    /// Reads the marker. Absent → null. Corrupt/unparseable → quarantined aside as
    /// <c>boot-refusal.json.quarantined-{ticks}</c> (never left in place to poison a later read)
    /// and null.
    /// </summary>
    public static BootRefusalRecord? TryRead(string stateDir) {
        var path = MarkerPath(stateDir);

        if (!File.Exists(path)) return null;

        try {
            return JsonSerializer.Deserialize(File.ReadAllText(path), BootRefusalJsonCtx.Default.BootRefusalRecord);
        } catch {
            try {
                File.Move(path, path + ".quarantined-" + DateTime.UtcNow.Ticks, overwrite: true);
            } catch {
                // best-effort quarantine
            }

            return null;
        }
    }

    /// <summary>Best-effort hygiene delete on a passing boot. Failure is the caller's to log, if it wants to.</summary>
    public static void TryDelete(string stateDir) {
        try { File.Delete(MarkerPath(stateDir)); } catch { /* best-effort */ }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(BootRefusalRecord))]
    partial class BootRefusalJsonCtx : JsonSerializerContext;
}
