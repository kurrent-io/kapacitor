using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The per-launch isolated <c>KIRO_HOME</c> for an unattended Kiro reviewer.
///
/// <para><b>Why an isolated home at all.</b> Kiro inherits the operator's GLOBAL
/// <c>~/.kiro/settings/mcp.json</c> servers into every ACP session — measured with a positive control
/// in <c>docs/probes/2026-08-05-kiro-reviewer-trust/</c>. One of them is the flows server, which
/// would let a reviewer start nested review flows. Pointing <c>KIRO_HOME</c> at an empty directory
/// initializes ZERO global servers while the injected result channel still starts. The credential is
/// NOT under <c>KIRO_HOME</c>, so this suppresses configuration without touching authentication —
/// which is the whole reason this approach works where an OS sandbox did not.</para>
///
/// <para><b>Why disposal is a security requirement, not hygiene.</b> <c>KiroPaths.ConfigRoot</c>
/// reads <c>KIRO_HOME</c> first, so the reviewer's own conversation JSONL lands in
/// <c>{KIRO_HOME}/sessions/cli</c> — carrying the caller's diff, source excerpts and findings. The
/// home is read-EMPTY but write-SENSITIVE, on a host that may serve several callers.</para>
///
/// <para><b>The root is per daemon, and that is load-bearing.</b> An earlier design specified one
/// shared root swept by epoch, reasoning that the epoch key made it safe for a second daemon on the
/// same host. The opposite is true: with a shared root, daemon A's rule "delete every home whose
/// epoch is not mine" selects daemon B's CURRENT, LIVE home, because B's epoch is not A's. Nor can
/// reap-before-delete rescue it — A must not signal a process owned by an unrelated live daemon.
/// Per-daemon roots remove the question instead of adjudicating it: every directory in this root
/// belongs to an incarnation of THIS daemon, so a non-current epoch is by definition dead.</para>
/// </summary>
internal static class KiroReviewerHome {
    const string Prefix = "kcap-kiro-reviewer-";

    /// <summary>This daemon's own reviewer-home root. Never shared with a peer daemon.</summary>
    internal static string RootFor(string stateDir) => Path.Combine(stateDir, "kiro-reviewers");

    /// <summary>
    /// Creates an empty, owner-only home. Empty is what makes the global-MCP suppression work, so
    /// nothing may ever be seeded into it.
    /// </summary>
    internal static string Create(string stateDir, string daemonEpoch, string launchId) {
        var root = RootFor(stateDir);
        Directory.CreateDirectory(root);
        Harden(root);

        var home = Path.Combine(root, $"{Prefix}{Sanitize(daemonEpoch)}-{Sanitize(launchId)}");
        Directory.CreateDirectory(home);

        // Hardened immediately after creation, before the child can write a transcript line into it.
        // A world-readable window between mkdir and chmod is long enough to leak review context.
        Harden(home);
        return home;
    }

    /// <summary>
    /// Deletes every home in THIS daemon's root whose epoch is not the current one — the crash and
    /// SIGKILL recovery. Safe by construction because the root is not shared.
    /// </summary>
    internal static void SweepStale(string stateDir, string currentEpoch, ILogger log) {
        var root = RootFor(stateDir);
        if (!Directory.Exists(root)) return;

        var live = $"{Prefix}{Sanitize(currentEpoch)}-";

        foreach (var dir in Directory.EnumerateDirectories(root)) {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (name.StartsWith(live,   StringComparison.Ordinal)) continue;

            Delete(dir, stateDir, log);
        }
    }

    /// <summary>
    /// Removes a reviewer home. Never follows a link out of the tree, and refuses a path that does
    /// not resolve inside this daemon's root. Both are standard recursive-delete hazards; the
    /// transcript content is why they are requirements here rather than theoretical.
    /// </summary>
    internal static void Delete(string homePath, string stateDir, ILogger log) {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootFor(stateDir)));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(homePath));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            log.LogWarning("Kiro reviewer home {Path} resolves outside {Root}; refusing to delete", full, root);
            return;
        }

        try {
            DeleteTreeNoFollow(full);
        } catch (Exception ex) {
            // Log and continue: an undeletable home must not fail a round or block daemon startup.
            // It IS undisposed review context accumulating on disk, so it is never silent.
            log.LogWarning(ex, "Failed to delete Kiro reviewer home {Path}", full);
        }
    }

    /// <summary>
    /// Recursive delete that unlinks a directory symlink rather than descending through it. Kind is
    /// read from the attributes, which do NOT follow — <c>Directory.Exists</c> does, and would report
    /// false for a dangling directory link, falling through to a <c>File.Delete</c> that a Windows
    /// reparse point rejects.
    /// </summary>
    static void DeleteTreeNoFollow(string path) {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path)) {
            var attributes  = new FileInfo(entry).Attributes;
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isLink      = attributes.HasFlag(FileAttributes.ReparsePoint);

            if (isDirectory && isLink) Directory.Delete(entry);   // the link; its target is untouched
            else if (isDirectory)      DeleteTreeNoFollow(entry);
            else                       File.Delete(entry);
        }

        Directory.Delete(path);
    }

    static void Harden(string path) {
        if (OperatingSystem.IsWindows()) return;

        try {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        } catch {
            // Best-effort, as LaunchConsentStore does for its own state dir and for the same reason:
            // a mode we cannot set must not stop the daemon, and the caller's own gate (the POSIX-only
            // platform check) is what keeps this from being the only protection.
        }
    }

    static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
