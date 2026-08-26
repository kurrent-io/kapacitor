using System.Text.Json;

namespace Capacitor.Cli.Core;

/// <summary>
/// Stable per-machine identifier: lets the server prove that a daemon reconnecting
/// under a given repo path is actually running on the machine it claims, rather than trusting a
/// path string alone. Persisted once to <c>machine.json</c> in the CLI's config directory
/// (the <see cref="ConfigRoot"/> it is handed — the same one <c>Auth.TokenStore</c> takes) so every
/// process on this machine — hooks, watcher, daemon, MCP — resolves the same file and reports the
/// same id.
///
/// Distinct from <see cref="MachineIdProvider"/>, which tags memories with a
/// separately-generated id stored inside the profile config (<c>config.json</c>'s
/// <c>machine_id</c>); the two are not reconciled. This one is a standalone file so reading/
/// writing it can never race a concurrent <c>ProfileConfig</c> save.
/// </summary>
public sealed class MachineId(ConfigRoot config) {
    string MachinePath { get; } = config.Path("machine.json");

    /// <summary>
    /// Returns this machine's stable id, generating and persisting one on first call. Later
    /// calls — in this process or a new one — read the same persisted value back.
    /// </summary>
    public string Get() => ReadPersisted() ?? Create();

    /// <summary>
    /// Reads the persisted id straight off disk — what a fresh process (or a fresh call after a
    /// peer process wrote it) would see. Returns null if machine.json doesn't exist yet or is
    /// corrupt (never resurrects a partial/garbled write; the next <see cref="Get"/> just
    /// regenerates).
    /// </summary>
    public string? ReadPersisted() {
        if (!File.Exists(MachinePath)) return null;

        try {
            var json = File.ReadAllText(MachinePath);
            var file = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.MachineIdFile);
            return string.IsNullOrWhiteSpace(file.Id) ? null : file.Id;
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            // Corrupt/partial JSON, or a transient read error while a peer holds the file exclusively
            // mid-write — treat all as "no readable id right now"; never throw out of Get() (Qodo #290 #1).
            return null;
        }
    }

    // Two processes can race to create machine.json on a brand-new machine (e.g. the watcher and
    // the daemon starting at once). FileMode.CreateNew is an exclusive OS-level create — exactly
    // one writer succeeds; the loser's create throws IOException and re-reads the file the
    // winner just wrote instead of keeping its own generated id, so both converge on one value
    // with no separate lock file needed.
    string Create() {
        var id  = Guid.NewGuid().ToString("N");
        var dir = Path.GetDirectoryName(MachinePath)!;
        Directory.CreateDirectory(dir);

        try {
            WriteId(FileMode.CreateNew, id);   // exclusive create — the brand-new-machine case
            return id;
        } catch (IOException) {
            // machine.json already exists: adopt a peer's valid id if we can read one (the
            // lost-the-race case — a peer created it between our ReadPersisted() check inside Get()
            // and this create).
            var peer = ReadPeerIdWithRetry();
            if (peer is not null) return peer;
            // ...else it's persistently unreadable (corrupt / stuck partial write). HEAL by overwriting
            // once so Get() returns a STABLE persisted id instead of churning a new GUID each call
            // (Qodo #290 #2). Best-effort: on a heal race we still return our id; the next Get() reads
            // whichever heal won.
            try { WriteId(FileMode.Create, id); } catch (IOException) { /* best effort */ }
            return id;
        }
    }

    void WriteId(FileMode mode, string id) {
        using var stream = new FileStream(MachinePath, mode, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(new MachineIdFile(id), CapacitorJsonContext.Default.MachineIdFile));
    }

    // Fallback read on the lost-race path. The winner holds the file exclusively (FileShare.None)
    // between its FileStream construction and disposal; a plain ReadPersisted() (File.ReadAllText
    // opens FileShare.Read) landing inside that sub-ms window fails to read and returns null (the
    // IOException is now swallowed there — Qodo #290 #1). Retry a few times with a tiny backoff so
    // the write completes and the id becomes readable; only return null (→ the caller heals with its
    // own generated id) if it stays genuinely unreadable past the budget.
    const int  PeerReadMaxAttempts = 10;
    const int  PeerReadDelayMs     = 5;

    string? ReadPeerIdWithRetry() {
        for (var attempt = 1; ; attempt++) {
            var peer = ReadPersisted();
            if (peer is not null || attempt >= PeerReadMaxAttempts) return peer;
            Thread.Sleep(PeerReadDelayMs);
        }
    }
}

/// <summary>On-disk shape of <c>machine.json</c> (see <see cref="MachineId"/>).</summary>
public readonly record struct MachineIdFile(string Id);
