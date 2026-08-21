using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon;

/// <summary>
/// Per-daemon-name exclusive lock and PID file. Acquired by the
/// daemon process for its lifetime; release happens automatically when the
/// <see cref="FileStream"/> handle closes (process exit, including
/// <c>SIGKILL</c> / power-off — the kernel releases <c>flock</c>).
///
/// <para>The lock content is the daemon's <c>InstanceId</c> (a fresh GUID
/// generated at startup). The server uses the same GUID — sent over
/// <c>DaemonConnect</c> — to distinguish a same-process reconnect from a
/// different-process collision. The lock file's content is therefore
/// diagnostic only (the wire <c>InstanceId</c> is the authoritative copy);
/// <c>kcap daemon doctor</c> reads it to surface human-friendly
/// "instance=<i>prefix</i>" output without needing a live SignalR session.</para>
///
/// <para>The acquisition guards against the scenario: two daemons
/// under the same name on the same machine (regardless of
/// <c>KCAP_CONFIG_DIR</c> — <see cref="DaemonStore"/> uses a fixed
/// directory). Two daemons with <i>different</i> names are allowed to
/// coexist; the lock file is per-name.</para>
/// </summary>
internal sealed class DaemonLock : IDisposable {
    readonly FileStream _stream;
    readonly string     _pidPath;
    readonly string     _lockPath;
    readonly string     _versionPath;
    bool                _disposed;

    public string InstanceId { get; }

    /// <summary>
    /// True when a PID file for this name was still on disk at the moment we
    /// acquired the exclusive flock. A graceful shutdown deletes the
    /// PID file in <see cref="Dispose"/>, so a leftover one means the previous
    /// holder exited WITHOUT running its cleanup — the signature of an
    /// uncatchable termination (external <c>SIGKILL</c> from macOS jetsam/OOM
    /// or <c>kill -9</c>, a power loss, or a hard native crash), none of which
    /// a signal handler inside the dying process can log. The successor logs a
    /// startup breadcrumb so the otherwise-silent death leaves a trace.
    /// </summary>
    public bool PriorExitWasUnclean { get; }

    /// <summary>
    /// The PID recorded in the leftover PID file, when <see cref="PriorExitWasUnclean"/>
    /// is true and the file's first line parsed as an integer; otherwise null.
    /// </summary>
    public int? PriorHolderPid { get; }

    /// <summary>
    /// The previous holder's <see cref="InstanceId"/>, read from the lock-file content
    /// under the held flock BEFORE we overwrote it — or null on a genuinely fresh lock
    /// (empty file) or unreadable content. Unlike the PID file (deleted on clean
    /// shutdown), the lock file's InstanceId is the persistent per-boot nonce every
    /// shipped version rewrites at boot and never deletes, so it witnesses the
    /// immediately-preceding boot even one by an unaware binary. Phase B2-b
    /// (sequenced-settlement design) uses it as the coverage boot-chain's chain-check.
    /// </summary>
    public string? PriorInstanceId { get; }

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.3): true iff the lock file was NON-EMPTY
    /// but its content could not be read into a prior InstanceId (a read fault, or blank/garbage
    /// content). Distinguishes an unreadable prior lock from a genuinely empty first-ever/genesis slot
    /// (where this is false and <see cref="PriorInstanceId"/> is null): the coverage boot-chain treats
    /// an indeterminate prior as fail-closed, NEVER as genesis.</summary>
    public bool PriorLockIndeterminate { get; }

    DaemonLock(FileStream stream, string lockPath, string pidPath, string versionPath, string instanceId,
               bool priorExitWasUnclean, int? priorHolderPid, string? priorInstanceId, bool priorLockIndeterminate) {
        _stream                = stream;
        _lockPath              = lockPath;
        _pidPath               = pidPath;
        _versionPath           = versionPath;
        InstanceId             = instanceId;
        PriorExitWasUnclean    = priorExitWasUnclean;
        PriorHolderPid         = priorHolderPid;
        PriorInstanceId        = priorInstanceId;
        PriorLockIndeterminate = priorLockIndeterminate;
    }

    /// <summary>
    /// Try to acquire the per-name lock for <paramref name="daemonName"/>.
    /// Returns null when another live daemon already holds the lock — the
    /// caller should print a "name in use" message and exit with code 2.
    ///
    /// <para>When <paramref name="version"/> is supplied, also writes the
    /// freely-readable <c>&lt;name&gt;.version</c> marker so <c>kcap daemon
    /// status</c> can report the running daemon's version.</para>
    /// </summary>
    public static DaemonLock? TryAcquire(DaemonStore store, string daemonName, string? version = null) {
        store.EnsureDirectory();

        var lockPath    = store.LockPath(daemonName);
        var pidPath     = store.PidPath(daemonName);
        var versionPath = store.VersionPath(daemonName);

        FileStream stream;

        try {
            // FileShare.None maps to flock(LOCK_EX) on POSIX. Open with
            // FileAccess.ReadWrite so we can both read the previous holder's
            // instance id (captured below, before truncation) and rewrite our
            // own into the file content. FileMode.OpenOrCreate keeps a stale
            // lockfile on disk from blocking acquisition — flock semantics
            // mean kernel-managed liveness, not file presence.
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        } catch (IOException) {
            return null;
        }

        var instanceId = Guid.NewGuid().ToString("N");

        // inspect any leftover PID file BEFORE WritePidFile overwrites
        // it. Now that we hold the exclusive flock the prior holder is provably
        // gone, so a still-present PID file means it never ran Dispose (which
        // deletes it) — i.e. it died via an uncatchable SIGKILL / crash. Capture
        // that for the successor's startup breadcrumb.
        var (priorUnclean, priorPid) = InspectPriorHolder(pidPath);

        // Capture the previous holder's InstanceId from the lock-file content BEFORE we overwrite it —
        // the persistent per-boot nonce the coverage boot-chain uses to detect an intervening
        // (possibly unaware) boot. A genuinely EMPTY file is a first-ever/genesis slot (priorInstanceId
        // stays null, NOT indeterminate). A non-empty file we cannot read into an id — a read fault OR
        // blank/garbage content — is INDETERMINATE: the coverage boot-chain must fail-closed there and
        // never conflate it with genesis. The read is bounded (the content is a 32-char GUID + newline),
        // so a corrupt/oversized lock file can't drive an unbounded allocation.
        string? priorInstanceId = null;
        var priorLockIndeterminate = false;
        try {
            if (stream.Length > 0) {
                stream.Position = 0;
                var len = (int)Math.Min(stream.Length, 4096);
                var buf = new byte[len];
                var n = stream.Read(buf, 0, buf.Length);
                priorInstanceId = System.Text.Encoding.UTF8.GetString(buf, 0, n)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                priorLockIndeterminate = string.IsNullOrEmpty(priorInstanceId); // non-empty file, no id ⇒ indeterminate
            }
        } catch {
            priorInstanceId = null;
            priorLockIndeterminate = true; // non-empty-but-unreadable ⇒ fail-closed, never genesis
        }

        try {
            // Rewrite the file content with the fresh instance id. Truncate
            // first so a smaller new id doesn't leave trailing bytes from
            // the previous holder.
            stream.SetLength(0);
            var bytes = System.Text.Encoding.UTF8.GetBytes(instanceId + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();

            WritePidFile(pidPath);
        } catch {
            stream.Dispose();
            throw;
        }

        // Overwrite (atomically) any stale version marker with ours. A successor
        // of a restart-after-update lands here and refreshes it to the new
        // binary's version. Best-effort and OUTSIDE the fatal try above: the
        // marker is observability-only (`kcap daemon status`), so an IO failure
        // writing it must never abort acquisition the way a lock/pid failure does.
        if (version is not null) {
            try { DaemonVersionMarker.Write(store, daemonName, version); } catch { /* best-effort */ }
        }

        return new DaemonLock(stream, lockPath, pidPath, versionPath, instanceId, priorUnclean, priorPid, priorInstanceId, priorLockIndeterminate);
    }

    /// <summary>
    /// Reads a leftover PID file (call only while holding the flock, before
    /// overwriting it). Returns (unclean: whether the file was present at all,
    /// pid: its parsed first line if any). A present file ⇒ the prior holder
    /// skipped its cleanup ⇒ unclean exit; the PID may be unparseable, in which
    /// case we still report unclean but with a null PID.
    /// </summary>
    static (bool unclean, int? pid) InspectPriorHolder(string pidPath) {
        bool present;
        try {
            present = File.Exists(pidPath);
        } catch {
            // Can't even stat it — don't fabricate a breadcrumb.
            return (false, null);
        }

        if (!present) return (false, null);

        // Presence is the unclean-exit signal; parsing the PID is secondary. A
        // present-but-unreadable file (permissions, transient IO, corruption)
        // still means the prior holder skipped cleanup — report unclean with an
        // unknown PID rather than silently masking a real hard-death as clean.
        // Read only the first line (the PID; the daemon writes "{pid}\n{token}")
        // instead of slurping the whole file, so a corrupt/oversized file can't
        // force a large allocation on every startup.
        try {
            using var reader = File.OpenText(pidPath);
            var first = reader.ReadLine();

            return (true, int.TryParse(first, out var pid) ? pid : null);
        } catch {
            return (true, null);
        }
    }

    /// <summary>
    /// Like <see cref="TryAcquire(DaemonStore, string, string?)"/> but retries until <paramref name="awaitTimeout"/>
    /// elapses — used by a self-respawned successor (<c>--await-lock</c>) to wait out the
    /// outgoing daemon's flock instead of exiting with code 2 on the first contended attempt.
    /// </summary>
    public static DaemonLock? TryAcquire(DaemonStore store, string daemonName, TimeSpan awaitTimeout, string? version = null) {
        var deadline = DateTime.UtcNow + awaitTimeout;

        while (true) {
            if (TryAcquire(store, daemonName, version) is { } locked) return locked;
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(100);
        }
    }

    static void WritePidFile(string pidPath) {
        // The second line is a cross-process-stable start token: on
        // Linux Process.StartTime differs between the daemon that writes it and
        // the CLI that later reads it, so we persist the kernel's boot-relative
        // starttime instead. Best-effort — a null token falls back to PID-only,
        // and the CLI's IsOurDaemon then uses a process-name check.
        var token   = ProcessStartToken.ForCurrent();
        var content = token is not null ? $"{Environment.ProcessId}\n{token}" : Environment.ProcessId.ToString();
        File.WriteAllText(pidPath, content);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        // delete the PID file (and version marker) BEFORE releasing the
        // flock, not after. A successor waiting on the lock (`--await-lock`
        // restart-after-update) can't acquire it until we release below, so by
        // then our PID file is already gone — it can never observe a leftover PID
        // file from our *clean* shutdown and misread it as an unclean death.
        // That makes "PID file present once you hold the flock" an unambiguous
        // uncatchable-kill signal (see TryAcquire), so the successor's startup
        // breadcrumb no longer needs to special-case the handoff. Because we
        // still hold the flock here, no other daemon can have rewritten these
        // files, so the ownership check below always passes for us — it stays as
        // defence-in-depth (e.g. a stale PID file we never owned).
        bool ours;
        try { ours = PidFileMatchesCurrentProcess(_pidPath); } catch { ours = false; }

        if (ours) {
            try { File.Delete(_pidPath); } catch { /* best-effort */ }
            try { File.Delete(_versionPath); } catch { /* best-effort */ }
        }

        // Release the kernel-level flock last. Do NOT delete the lock file: the
        // kernel flock is what enforces exclusion, file presence on disk is
        // irrelevant, and unlinking it here would race a daemon that acquired the
        // path between our unlink and a re-create — reopening the hole.
        // `kcap daemon doctor --clean` removes truly stale files.
        try { _stream.Dispose(); } catch { /* best-effort */ }
    }

    static bool PidFileMatchesCurrentProcess(string pidPath) {
        try {
            if (!File.Exists(pidPath)) return false;

            var line = File.ReadAllText(pidPath)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            return line is not null
                && int.TryParse(line, out var pid)
                && pid == Environment.ProcessId;
        } catch {
            return false;
        }
    }
}
