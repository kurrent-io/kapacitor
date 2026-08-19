using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core;

/// <summary>
/// The one owner of the boot-refusal marker: its name, its shape, its serializer, and every read and
/// write of it.
///
/// <para>It lives in Core because the three processes that touch it cannot reference each other — the
/// daemon writes it, the CLI reads it to attribute a service-verb readiness timeout, and the desktop
/// app reads it to attribute a detached start. Each used to carry its own record, its own
/// <c>JsonSerializerContext</c> and its own copy of the filename, and they had already drifted: the
/// CLI's record omitted <c>schema</c>, so it could not make the schema check the app made.</para>
///
/// <para>Every entry point is contained. The daemon writes this from the pre-host boot-check block,
/// before the logging pipeline or DI exist, and a boot refusal is itself an
/// "unwritable/misconfigured state dir" kind of condition — so the writer must never throw on top of
/// the refusal it is reporting.</para>
/// </summary>
public static partial class BootRefusalMarker {
    public const int CurrentSchema = 1;

    /// <summary>
    /// Best-effort atomic temp+rename write. NEVER throws. Deliberately does NOT create the state
    /// directory — that is the caller's job, done once up front so this call has somewhere to land
    /// even for a brand-new daemon name; manufacturing it here would blur "the directory exists" with
    /// "the directory is safe to exist".
    /// </summary>
    public static void TryWrite(
            DaemonStore store, string daemonName, string token,
            string? expectation, string? resolved, string? instanceId, string? attemptId) {
        try {
            var record = new BootRefusalRecord(
                CurrentSchema, daemonName, token, expectation, resolved,
                Environment.ProcessId, instanceId, attemptId, DateTimeOffset.UtcNow);

            var path = store.BootRefusalPath(daemonName);
            var tmp  = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

            File.WriteAllText(tmp, JsonSerializer.Serialize(record, BootRefusalJsonCtx.Default.BootRefusalRecord));
            File.Move(tmp, path, overwrite: true);
        } catch {
            // Contained: see class doc. There is nothing to log to at this point in the boot.
        }
    }

    /// <summary>
    /// Absent or corrupt → null, and a corrupt marker is LEFT IN PLACE: a reader that does not own the
    /// file has no authority to rename it aside. Opens share-all, never <c>File.ReadAllText</c> — the
    /// daemon owns this file and may concurrently rename/rewrite it, and a write-denying open would
    /// stall that on Windows.
    /// </summary>
    public static BootRefusalRecord? TryRead(DaemonStore store, string daemonName) {
        try {
            using var stream = new FileStream(store.BootRefusalPath(daemonName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            return JsonSerializer.Deserialize(reader.ReadToEnd(), BootRefusalJsonCtx.Default.BootRefusalRecord);
        } catch {
            // Missing or corrupt — both answer null, left in place.
            return null;
        }
    }

    /// <summary>
    /// VERIFIED delete: removes whatever sits at the marker path, then re-checks — false if anything
    /// (a locked file, a permission-denied file, or a directory <see cref="File.Delete(string)"/>
    /// cannot remove) is still there. A caller that gets false must treat any marker later found here
    /// as untrustworthy: it may be stale residue rather than fresh evidence.
    /// </summary>
    public static bool TryClear(DaemonStore store, string daemonName) {
        var path = store.BootRefusalPath(daemonName);
        try { File.Delete(path); } catch { /* verified below, not here */ }

        return !File.Exists(path) && !Directory.Exists(path);
    }

    /// <summary>Best-effort delete — hygiene on a passing boot, or a consume after attribution has
    /// already been reported. Failure must never affect an exit code or emit anything.</summary>
    public static void TryDelete(DaemonStore store, string daemonName) {
        try { File.Delete(store.BootRefusalPath(daemonName)); } catch { /* best-effort */ }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(BootRefusalRecord))]
    partial class BootRefusalJsonCtx : JsonSerializerContext;
}
