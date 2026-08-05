using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    /// <summary>One derivation of a home's directory name. Create and delete both go through it, so
    /// a failed launch's cleanup cannot target a path the launch never made.</summary>
    internal static string NameFor(string daemonEpoch, string launchId) =>
        $"{Prefix}{Sanitize(daemonEpoch)}-{Sanitize(launchId)}";

    internal static string Create(string stateDir, string daemonEpoch, string launchId) {
        var root = RootFor(stateDir);
        CreateOwnerOnly(root);

        var home = Path.Combine(root, NameFor(daemonEpoch, launchId));

        // A repeated launch under the same epoch+agent id must not inherit the previous one's
        // transcript: CreateDirectory silently succeeds on an existing directory, so "empty" would be
        // a hope rather than a property. Remove first, then create.
        if (Path.Exists(home)) Delete(home, stateDir, NullLogger.Instance);

        CreateOwnerOnly(home);

        // Delete is best-effort by design (an undeletable home must not fail a round), so a partial
        // failure could leave the previous launch's transcript in place and CreateOwnerOnly would
        // happily accept the surviving directory. Emptiness is the mechanism that suppresses the
        // operator's global MCP servers AND the guarantee that one review cannot read another's
        // context, so it is verified rather than assumed.
        if (Directory.EnumerateFileSystemEntries(home).Any())
            throw new InvalidOperationException(
                $"kiro_reviewer_home_not_empty: '{home}' still holds content from a previous launch that "
              + "could not be removed. Refusing rather than handing a reviewer another review's context, "
              + "or the global MCP servers an empty home exists to suppress.");

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

        // Enumeration itself can throw on an unreadable or corrupt root, BEFORE any per-directory
        // Delete gets a chance to catch. This runs at daemon boot, so an inaccessible reviewer root
        // must degrade to "swept nothing" rather than take the daemon down with it.
        IReadOnlyList<string> candidates;

        try {
            candidates = [.. Directory.EnumerateDirectories(root)];
        } catch (Exception ex) {
            log.LogWarning(ex, "Could not enumerate the Kiro reviewer home root {Root}; skipping the sweep", root);
            return;
        }

        foreach (var dir in candidates) {
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
    /// <para><b>Residual, accepted and documented rather than coded against:</b> every check here is
    /// check-then-use. A component swapped for a symlink between reading its attributes and opening
    /// it would be followed. Closing that needs <c>unlinkat</c>-style handle semantics .NET does not
    /// expose — the same residual <c>WorktreeManager.NeutralizeWorkspaceMcpConfig</c> records for the
    /// same reason. The window is narrow here specifically: the path lives under the daemon's own
    /// state directory, so winning the race needs an already-compromised process running as the
    /// daemon user, which has this authority regardless.</para>
    internal static void Delete(string homePath, string stateDir, ILogger log) {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootFor(stateDir)));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(homePath));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            log.LogWarning("Kiro reviewer home {Path} resolves outside {Root}; refusing to delete", full, root);
            return;
        }

        try {
            // The containment check above is LEXICAL, so a link planted AT the home path resolves
            // inside the root and would then be enumerated — following it into the target and
            // deleting the target's contents. DeleteTreeNoFollow protects nested entries but takes
            // the top-level path on trust, so the top level is checked here.
            var attributes = new FileInfo(full).Attributes;

            if (attributes.HasFlag(FileAttributes.ReparsePoint)) {
                if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(full);
                else                                             File.Delete(full);

                return;
            }

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

    const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates a directory that is owner-only FROM ITS FIRST INSTANT, and verifies it.
    ///
    /// <para>Not create-then-chmod: that leaves a window in which the directory exists at the default
    /// umask, and the thing written into it is review context. .NET's mode-carrying overload closes
    /// the window at the syscall.</para>
    ///
    /// <para>And not best-effort. An earlier revision swallowed every mode failure, on the LaunchConsentStore
    /// precedent — but that store degrades to a safe default, whereas here the mode IS the protection.
    /// A POSIX host where it cannot be achieved must not run a reviewer at all, so this throws and the
    /// launch fails.</para>
    /// </summary>
    static void CreateOwnerOnly(string path) {
        if (OperatingSystem.IsWindows()) {
            // Unreachable in production: KiroReviewerCapability refuses Windows before any launch.
            // Kept total so a direct call in a test cannot silently create a world-readable directory.
            throw new PlatformNotSupportedException(
                "kiro_reviewer_unsupported_platform: a reviewer home cannot be created owner-only here.");
        }

        Directory.CreateDirectory(path, OwnerOnly);

        // An existing directory keeps its own mode — CreateDirectory's mode argument applies only when
        // it creates. Verifying covers that, and any filesystem that ignores the request.
        var mode = File.GetUnixFileMode(path);

        if ((mode & (UnixFileMode.GroupRead  | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead  | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException(
                $"kiro_reviewer_home_not_owner_only: '{path}' is mode {mode}. The reviewer's transcript, "
              + "and so the review context, would be readable by other users on this host.");
    }

    static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
