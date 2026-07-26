using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Persists the tail of a hosted agent's PTY output when its launch FAILS, under
/// <c>{state-dir}/agents/failed/{sha256(agentId)}.log</c> — survives the failed launch's
/// worktree teardown, so the output that explained the failure (e.g. the consent dialog it
/// wedged on) is still available post-mortem instead of an hours-long blind debug.
///
/// The retained tail can hold the reviewer's prompt, tool output, and any secrets the PTY
/// echoed, so on Unix the directory is owner-only (0700) and the file owner-only (0600) —
/// stamped at creation via <c>UnixCreateMode</c> and re-asserted after the atomic rename, so
/// there's never a world-readable window. No-ops on Windows.
///
/// Best-effort: any I/O failure is swallowed and returns <c>null</c> — a diagnostic must never
/// fail a teardown. The filename is a SHA-256 of the (untrusted) agent id, not the id itself, so
/// no <c>..</c>/separator can escape the directory (same discipline as
/// <see cref="AgentPidRecordStore"/>). Plain file I/O — NativeAOT-safe.
/// </summary>
internal sealed class FailedLaunchLog(string stateDir, int maxBytes = 64 * 1024) {
    readonly string _dir = Path.Combine(stateDir, "agents", "failed");

    /// <summary>The retained failed-launch directory ({state}/agents/failed). Exposed for tests to
    /// assert a capture landed.</summary>
    public string Dir => _dir;

    /// <summary>
    /// Writes (overwriting any prior entry for this agent) a header line plus the last
    /// <c>maxBytes</c> of <paramref name="terminalOutput"/>. Returns the file path, or <c>null</c>
    /// if persisting failed.
    /// </summary>
    public string? Persist(string agentId, byte[] terminalOutput, string reason) {
        try {
            Directory.CreateDirectory(_dir);

            // Owner-only directory: 0700 stops other local users traversing in to the 0600 files.
            if (!OperatingSystem.IsWindows()) {
                try { File.SetUnixFileMode(_dir, DirMode); } catch { /* best-effort */ }
            }

            var path = Path.Combine(_dir, SafeName(agentId) + ".log");
            var tail = terminalOutput.Length > maxBytes
                ? terminalOutput[^maxBytes..]
                : terminalOutput;

            var header = Encoding.UTF8.GetBytes(
                $"# kcap failed-launch capture\n" +
                $"# time:   {DateTimeOffset.UtcNow:O}\n" +
                $"# agent:  {agentId}\n" +
                $"# reason: {reason}\n" +
                $"# --- last {tail.Length} bytes of PTY output follow ---\n");

            // Temp + rename so a concurrent reader never sees a half-written capture. The temp is
            // created 0600 from its first byte (UnixCreateMode) — a chmod after writing would leave a
            // window where the secret-bearing tail is world-readable — and the atomic rename carries
            // 0600 onto the final file.
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileMode0600;

            using (var fs = new FileStream(tmp, options)) {
                fs.Write(header);
                fs.Write(tail);
            }

            File.Move(tmp, path, overwrite: true);

            // Re-assert 0600 on the published path: `overwrite: true` may have replaced a pre-existing
            // final file, and this closes any platform gap in what the rename carries across.
            if (!OperatingSystem.IsWindows()) {
                try { File.SetUnixFileMode(path, FileMode0600); } catch { /* best-effort */ }
            }

            return path;
        } catch {
            return null; // best-effort — never fail a teardown over a diagnostic write
        }
    }

    const UnixFileMode FileMode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode DirMode      = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    static string SafeName(string agentId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentId ?? ""))).ToLowerInvariant();
}
