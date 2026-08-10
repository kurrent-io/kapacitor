using System.Text.Json;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>On-disk shape of the device-id file (see <see cref="TelemetryDeviceId"/>).</summary>
public readonly record struct TelemetryDeviceIdFile(string Id);

/// <summary>
/// Owns the anonymous analytics device id in its own file, deliberately separate from
/// <c>telemetry.json</c>'s <c>Enabled</c>/<c>NoticeShown</c> fields (see <see cref="TelemetryState"/>).
/// A device id is a single immutable random value with no consistency relationship to anything
/// else: if two processes race to create it, either GUID is fine so long as one wins and every
/// reader converges on it. That means it needs none of <see cref="TelemetryState"/>'s
/// <c>ConfigFileLock</c>-guarded read-modify-write or atomic temp-file-then-rename — machinery that
/// exists for the enable flag, where a lost update silently clobbers a user's opt-out.
///
/// The pattern below is lifted from <see cref="MachineId"/>: <see cref="FileMode.CreateNew"/> is an
/// exclusive OS-level create, so exactly one racing writer wins; the loser re-reads the winner's
/// value (with a small retry, since the winner holds the file only briefly); a persistently
/// unreadable/corrupt file heals by overwriting once, so the id stays stable rather than churning a
/// new GUID per call. Unlike <see cref="MachineId"/>, every path here is wrapped so it can never
/// throw — telemetry's global constraint, since an exception escaping to the NativeAOT runtime
/// aborts the whole process.
/// </summary>
public static class TelemetryDeviceId {
    /// <summary>Test seam. Null in production, where the path resolves under the config dir.</summary>
    public static string? PathOverride { get; set; }

    static string Path => PathOverride ?? PathHelpers.ConfigPath("telemetry-device.json");

    /// <summary>
    /// Returns the stable device id, creating and persisting one on first call. Returns null only
    /// when the id could neither be read, created, nor healed (e.g. the config directory itself is
    /// unwritable) — callers must fall back to an in-memory-only id rather than treat null as a
    /// reason to disable telemetry (see <c>CliTelemetry.Initialize</c>): a disk hiccup here costs a
    /// marginally inflated unique-device count, not an entire session's worth of events.
    /// </summary>
    public static string? GetOrCreate() {
        try {
            return ReadPersisted() ?? Create();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Reads the persisted id straight off disk — what a fresh process (or a fresh call after a
    /// peer process wrote it) would see. Returns null if the file doesn't exist yet or is corrupt.
    /// </summary>
    public static string? ReadPersisted() {
        var path = Path;
        if (!File.Exists(path)) return null;

        try {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.TelemetryDeviceIdFile);
            return string.IsNullOrWhiteSpace(file.Id) ? null : file.Id;
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            // Corrupt/partial JSON, or a transient read error while a peer holds the file
            // exclusively mid-write — treat all as "no readable id right now".
            return null;
        }
    }

    // Two processes can race to create the device file on a brand-new machine (e.g. the CLI and a
    // long-lived MCP server starting at once). FileMode.CreateNew is an exclusive OS-level create —
    // exactly one writer succeeds; the loser's create throws IOException and re-reads the file the
    // winner just wrote instead of keeping its own generated id, so both converge on one value with
    // no separate lock file needed (mirrors MachineId.Create).
    static string Create() {
        var path = Path;
        var id   = Guid.NewGuid().ToString("N");
        var dir  = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        try {
            WriteId(path, FileMode.CreateNew, id);
            return id;
        } catch (IOException) {
            // File already exists: adopt a peer's valid id if we can read one (the lost-the-race
            // case — a peer created it between our ReadPersisted() check and this create).
            var peer = ReadPeerIdWithRetry();
            if (peer is not null) return peer;
            // ...else it's persistently unreadable (corrupt / stuck partial write). Heal by
            // overwriting once so GetOrCreate returns a STABLE id instead of churning a new GUID
            // per call. Best effort: on a heal race we still return our own id; the next call reads
            // whichever heal won.
            try { WriteId(path, FileMode.Create, id); } catch (IOException) { /* best effort */ }
            return id;
        }
    }

    static void WriteId(string path, FileMode mode, string id) {
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(new TelemetryDeviceIdFile(id), CapacitorJsonContext.Default.TelemetryDeviceIdFile));
    }

    // Fallback read on the lost-race path. The winner holds the file exclusively (FileShare.None)
    // between its FileStream construction and disposal; a plain ReadPersisted() landing inside that
    // sub-ms window fails to read and returns null. Retry a few times with a tiny backoff so the
    // write completes and the id becomes readable; only return null (→ the caller heals with its
    // own generated id) if it stays genuinely unreadable past the budget.
    const int PeerReadMaxAttempts = 10;
    const int PeerReadDelayMs     = 5;

    static string? ReadPeerIdWithRetry() {
        for (var attempt = 1; ; attempt++) {
            var peer = ReadPersisted();
            if (peer is not null || attempt >= PeerReadMaxAttempts) return peer;
            Thread.Sleep(PeerReadDelayMs);
        }
    }

    /// <summary>
    /// Deletes the device id file, if present. Called when telemetry is disabled: the spec's
    /// rationale for keeping the analytics id out of <c>machine.json</c> is that opt-out can delete
    /// it outright, not merely stop minting new ones. No lock needed — a delete racing a concurrent
    /// create just means the next reader either sees the survivor or mints a fresh id, both
    /// acceptable outcomes for a value with no consistency requirement.
    /// </summary>
    public static void Delete() {
        try {
            var path = Path;
            if (File.Exists(path)) File.Delete(path);
        } catch {
            // Best effort. Telemetry must never throw to the NativeAOT runtime.
        }
    }
}
