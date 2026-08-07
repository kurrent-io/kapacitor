using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The per-launch isolated <c>OPENCODE_CONFIG_DIR</c> for an unattended OpenCode reviewer — the
/// direct analogue of <see cref="KiroReviewerHome"/>, and empty for the same reason.
///
/// <para><b>Why an isolated config dir.</b> OpenCode reads the operator's global configuration,
/// <c>mcp</c> servers included, into every session. One of them is the flows server, which would let a
/// reviewer start nested review flows. Pointing <c>OPENCODE_CONFIG_DIR</c> at an empty directory
/// removes that whole source while the result channel injected through
/// <c>session/new.mcpServers</c> still starts.</para>
///
/// <para><b>Credentials are NOT under it</b> — measured
/// (<c>docs/probes/2026-08-07-opencode-acp/</c> §5): an empty config dir still completes
/// <c>initialize</c> and <c>session/new</c> with the account's full model list. That is the fact this
/// approach depends on, and it is why the Kiro-shaped answer transfers to OpenCode at all; without it,
/// isolating configuration would have disabled the reviewer.</para>
///
/// <para><b>Why not one shared empty directory.</b> It would be simpler, and OpenCode is not currently
/// observed to write here (a default <c>config.json</c> is auto-created only when NONE of
/// <c>OPENCODE_CONFIG</c>/<c>OPENCODE_CONFIG_DIR</c>/<c>OPENCODE_CONFIG_CONTENT</c> is set). Two
/// reasons not to rely on that: a future release that does write here would silently carry one
/// reviewer's state into the next, and a shared root reintroduces the peer-daemon problem
/// <see cref="KiroReviewerHome"/> documents — "delete every directory whose epoch is not mine" selects
/// a peer daemon's LIVE directory. Per-daemon, per-launch removes both questions instead of
/// adjudicating them.</para>
///
/// <para><b>Emptiness is the mechanism, so it is verified rather than assumed.</b> Delete is
/// best-effort (an undeletable directory must not fail a round), so a partial failure could leave
/// content behind and <c>CreateDirectory</c> would happily accept the survivor.</para>
/// </summary>
internal static class OpenCodeReviewerConfigDir {
    const string Prefix = "kcap-opencode-reviewer-";

    /// <summary>This daemon's own reviewer-config root. Never shared with a peer daemon.</summary>
    internal static string RootFor(string stateDir) => Path.Combine(stateDir, "opencode-reviewers");

    /// <summary>One derivation of a directory's name. Create and delete both go through it, so a
    /// failed launch's cleanup cannot target a path the launch never made.</summary>
    internal static string NameFor(string daemonEpoch, string launchId) =>
        $"{Prefix}{Sanitize(daemonEpoch)}-{Sanitize(launchId)}";

    /// <summary>Creates an empty, owner-only config dir. Empty is what suppresses the operator's
    /// global MCP servers, so nothing may ever be seeded into it.</summary>
    internal static string Create(string stateDir, string daemonEpoch, string launchId, ILogger? log = null) {
        var root = RootFor(stateDir);
        CreateOwnerOnly(root);

        var dir = Path.Combine(root, NameFor(daemonEpoch, launchId));

        // A repeated launch under the same epoch+agent id must not inherit whatever the previous one
        // left: CreateDirectory silently succeeds on an existing directory, so "empty" would be a hope
        // rather than a property. Remove first, then create.
        if (Path.Exists(dir)) Delete(dir, stateDir, log ?? NullLogger.Instance);

        CreateOwnerOnly(dir);

        if (Directory.EnumerateFileSystemEntries(dir).Any())
            throw new InvalidOperationException(
                $"opencode_reviewer_config_dir_not_empty: '{dir}' still holds content from a previous "
              + "launch that could not be removed. Refusing rather than handing a reviewer the global "
              + "MCP servers an empty config dir exists to suppress.");

        return dir;
    }

    /// <summary>
    /// Deletes every directory in THIS daemon's root whose epoch is not the current one — the crash and
    /// SIGKILL recovery. Safe by construction because the root is not shared.
    /// </summary>
    /// <para><b>Known ordering, accepted:</b> runs at daemon start, before the orphan-agent reaper —
    /// the same accepted residual <see cref="KiroReviewerHome.SweepStale"/> records.</para>
    internal static void SweepStale(string stateDir, string currentEpoch, ILogger? log = null) {
        log ??= NullLogger.Instance;
        var root = RootFor(stateDir);
        if (!Directory.Exists(root)) return;

        var live = $"{Prefix}{Sanitize(currentEpoch)}-";

        IReadOnlyList<string> candidates;

        try {
            candidates = [.. Directory.EnumerateDirectories(root)];
        } catch (Exception ex) {
            log.LogWarning(ex,
                "Could not enumerate the OpenCode reviewer config root {Root}; skipping the sweep", root);
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
    /// Removes a reviewer config dir. Never follows a link out of the tree, and refuses a path that
    /// does not resolve inside this daemon's root — the same two recursive-delete hazards
    /// <see cref="KiroReviewerHome.Delete"/> guards against, and it carries the same documented
    /// check-then-use residual for the same reason (closing it needs <c>unlinkat</c>-style handle
    /// semantics .NET does not expose, and the path lives under the daemon's own state directory).
    /// </summary>
    internal static void Delete(string dirPath, string stateDir, ILogger? log = null) {
        log ??= NullLogger.Instance;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootFor(stateDir)));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dirPath));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            log.LogWarning("OpenCode reviewer config dir {Path} resolves outside {Root}; refusing to delete",
                full, root);
            return;
        }

        try {
            // The containment check above is LEXICAL, so a link planted AT the path resolves inside the
            // root and would then be enumerated — following it into the target and deleting the
            // target's contents. DeleteTreeNoFollow protects nested entries but takes the top-level
            // path on trust, so the top level is checked here.
            var attributes = new FileInfo(full).Attributes;

            if (attributes.HasFlag(FileAttributes.ReparsePoint)) {
                if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(full);
                else                                             File.Delete(full);

                return;
            }

            DeleteTreeNoFollow(full);
        } catch (Exception ex) {
            // Log and continue: an undeletable directory must not fail a round or block startup.
            log.LogWarning(ex, "Failed to delete OpenCode reviewer config dir {Path}", full);
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
    /// Creates a directory that is owner-only FROM ITS FIRST INSTANT, and verifies it. Not
    /// create-then-chmod: that leaves a window at the default umask.
    ///
    /// <para>Not best-effort either — same reasoning as <see cref="KiroReviewerHome"/>: a POSIX host
    /// that cannot achieve the mode must not run a reviewer. Note the honest scope of the claim here:
    /// unlike Kiro's home this directory is not currently observed to receive review CONTENT, so the
    /// mode protects against a future release that writes here rather than a measured leak today. It is
    /// enforced anyway because "empty" is a security property and a group-writable directory is one
    /// another local user could seed a config into.</para>
    /// </summary>
    static void CreateOwnerOnly(string path) {
        if (OperatingSystem.IsWindows()) {
            // Unreachable in production: OpenCodeReviewerCapability refuses Windows before any launch.
            // Kept total so a direct call in a test cannot silently create a world-writable directory.
            throw new PlatformNotSupportedException(
                "opencode_reviewer_unsupported_platform: a reviewer config dir cannot be created "
              + "owner-only here.");
        }

        Directory.CreateDirectory(path, OwnerOnly);

        // An existing directory keeps its own mode — CreateDirectory's mode argument applies only when
        // it creates. Verifying covers that, and any filesystem that ignores the request.
        var mode = File.GetUnixFileMode(path);

        if ((mode & (UnixFileMode.GroupRead  | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead  | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException(
                $"opencode_reviewer_config_dir_not_owner_only: '{path}' is mode {mode}. Another local "
              + "user could seed configuration — MCP servers included — into a directory whose "
              + "EMPTINESS is the reviewer's containment.");
    }

    static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
