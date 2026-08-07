using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Antigravity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The per-launch isolated <c>HOME</c> for an unattended Antigravity (<c>agy</c>) reviewer.
///
/// <para><b>Four load-bearing jobs, not tidiness.</b></para>
/// <para>1. <b>Capture stays single-lane.</b> A probe confirmed <c>agy -p</c> really does load and
/// fire the kcap capture hooks it finds at <c>{HOME}/.gemini/config/plugins/kcap/hooks.json</c> — so
/// pointing a reviewer at the operator's real <c>HOME</c> would spawn a second watcher against the
/// SAME conversation the daemon is already recording, double-capturing it. This home never writes
/// that plugin directory, so the hook has nothing to load.</para>
/// <para>2. <b>No nested review flows.</b> Without a global <c>{HOME}/.gemini/config/mcp_config.json</c>,
/// the operator's <c>kcap-flows</c> server is absent, so a reviewer cannot start its own review flow —
/// the same reasoning <see cref="KiroReviewerHome"/> documents for Kiro's global <c>mcp.json</c>.</para>
/// <para>3. <b>Isolation.</b> The reviewer's own conversation state — <c>brain/</c>, <c>conversations/*.db</c>,
/// settings — lands under this HOME rather than the operator's real agy state.</para>
/// <para>4. <b>No hook ⇒ no watcher ⇒ nothing to leak.</b> A fired capture hook spawns a <c>kcap watch</c>
/// child; on an unfixed build that child inherits pipe descriptors and keeps the agent's OWN STDOUT
/// open after the agent process exits — measured at 5s to files versus still hung at 10 minutes to a
/// pipe. This runtime parses <c>agy</c>'s NDJSON from stdout once per turn, so that hang would block
/// every turn forever. This is why "just reuse the operator's real HOME" is not an available
/// simplification: an empty-of-hooks HOME is what keeps that failure mode structurally unreachable,
/// not something a code fix in the descriptor-leak path alone would close for a reviewer launch.</para>
///
/// <para><b>Where this differs from Kiro's home.</b> <see cref="KiroReviewerHome"/> stays literally
/// empty — Kiro reads its injected result-channel server off <c>session/new</c> directly. Antigravity
/// does not: this home carries a WRITTEN <c>mcp_config.json</c> holding only the injected servers
/// (in practice, the single <c>kcap-flow-result</c> submission channel) — job 2 above is what makes
/// writing that file safe: it replaces the operator's global file rather than merging into it, so the
/// reviewer's MCP surface is exactly the injected list, never the operator's own servers plus it. The
/// plugin directory job 1 depends on must still never exist, and creation verifies that rather than
/// assuming it.</para>
///
/// <para><b>Why disposal is a security requirement, not hygiene.</b> The reviewer's own conversation
/// JSONL (<c>brain/&lt;id&gt;/.system_generated/logs/transcript_full.jsonl</c>, see
/// <see cref="AntigravityPaths"/>) carries the caller's diff, source excerpts and findings. The home
/// is read-EMPTY-of-operator-config but write-SENSITIVE, on a host that may serve several callers.</para>
///
/// <para><b>The root is per daemon, and that is load-bearing</b> — same reasoning as
/// <see cref="KiroReviewerHome"/>: a shared root's "delete every home whose epoch is not mine" rule
/// would delete a peer daemon's CURRENT, LIVE home, because a peer's epoch is never this daemon's.
/// Per-daemon roots remove the question instead of adjudicating it.</para>
/// </summary>
internal static class AntigravityReviewerHome {
    const string Prefix = "kcap-antigravity-reviewer-";

    /// <summary>This daemon's own reviewer-home root. Never shared with a peer daemon.</summary>
    internal static string RootFor(string stateDir) => Path.Combine(stateDir, "antigravity-reviewers");

    /// <summary>One derivation of a home's directory name. Create and delete both go through it, so a
    /// failed launch's cleanup cannot target a path the launch never made.</summary>
    internal static string NameFor(string daemonEpoch, string launchId) =>
        $"{Prefix}{Sanitize(daemonEpoch)}-{Sanitize(launchId)}";

    /// <summary>
    /// Creates an owner-only home carrying only a fresh <c>mcp_config.json</c> for
    /// <paramref name="injected"/>. Never seeds the kcap plugin directory — that absence is what job
    /// 1 above depends on, so it is verified after writing rather than assumed.
    /// </summary>
    internal static string Create(string stateDir, string daemonEpoch, string launchId,
                                  IReadOnlyList<AcpMcpServerSpec> injected, ILogger? log = null) {
        var root = RootFor(stateDir);
        CreateOwnerOnly(root);

        var home = Path.Combine(root, NameFor(daemonEpoch, launchId));

        // A repeated launch under the same epoch+agent id must not inherit the previous one's
        // conversation state (brain/, conversations/*.db) or a stale mcp_config.json:
        // CreateDirectory silently succeeds on an existing directory, so "fresh" would be a hope
        // rather than a property. Remove first, then create.
        if (Path.Exists(home)) Delete(home, stateDir, log ?? NullLogger.Instance);

        CreateOwnerOnly(home);

        // Delete is best-effort by design (an undeletable home must not fail a round), so a partial
        // failure could leave the previous launch's brain/conversations content in place, and
        // CreateOwnerOnly would happily accept the surviving directory — it does not check
        // emptiness. A silently inherited conversation is the worst outcome this class can produce
        // (the reviewer would resume someone else's review with no signal), so freshness is
        // verified here, before anything is written, rather than assumed from "Delete ran".
        if (Directory.EnumerateFileSystemEntries(home).Any())
            throw new InvalidOperationException(
                $"antigravity_reviewer_home_not_empty: '{home}' still holds content from a previous "
              + "launch that could not be removed. Refusing rather than handing a reviewer another "
              + "review's conversation state.");

        WriteMcpConfig(home, injected);

        // The kcap plugin dir is what lets agy's OWN capture hooks fire (job 1) — its absence is
        // the whole mechanism, so it is checked rather than trusted to follow from "we never wrote
        // it". A future change that seeds a fuller home must trip this, not silently double-capture.
        if (Directory.Exists(AntigravityPaths.PluginDir(home)))
            throw new InvalidOperationException(
                $"antigravity_reviewer_home_not_isolated: '{home}' carries a kcap plugin directory, "
              + "which would let this reviewer's own capture hooks fire against its conversation.");

        return home;
    }

    /// <summary>Writes <c>{home}/.gemini/config/mcp_config.json</c> — the plain, trust-less
    /// <c>mcpServers</c> shape (<c>McpConfigShape.Standard</c>) — holding only <paramref name="injected"/>.
    /// A fresh write into a fresh directory, so unlike <c>JsonMcpConfigWriter</c> (which merges into
    /// and preserves an operator's live config) there is nothing to read-modify-write or mark as
    /// kcap-owned: the whole file is this launch's content, one shot, atomic-by-single-write.
    /// Built via <c>(JsonNode?)</c> string casts, not <c>JsonValue.Create</c> / collection expressions
    /// — the latter lower to a generic <c>Add&lt;T&gt;</c> that trips NativeAOT (IL3050), the same
    /// rule <c>ClaudeLauncher.BuildReviewFlowMcpConfig</c> and <c>ReviewLaunchBuilder</c> follow.</summary>
    static void WriteMcpConfig(string home, IReadOnlyList<AcpMcpServerSpec> injected) {
        var path = AntigravityPaths.McpConfigJson(home);

        // AntigravityPaths.McpConfigJson → GeminiPaths.Root honors THIS PROCESS's own
        // GEMINI_CLI_HOME when set, falling back to `home` only when it is not — so on a daemon
        // that happens to run with GEMINI_CLI_HOME set, the resolved path would escape `home`
        // entirely, writing the reviewer's result channel outside the isolated home job 3 exists
        // to guarantee (and potentially into the operator's own Gemini tree). Not something to fix
        // in AntigravityPaths (that fallback is correct for its other, non-isolated callers) — this
        // class's whole purpose is isolation, so it is verified here rather than assumed.
        var homeFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        var pathFull = Path.GetFullPath(path);

        if (!pathFull.StartsWith(homeFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"antigravity_reviewer_home_escaped_root: mcp_config.json resolved to '{pathFull}', "
              + $"outside the isolated home '{homeFull}' (likely GEMINI_CLI_HOME set in the daemon's "
              + "own environment). Refusing to write a reviewer's result channel outside its isolated home.");

        Directory.CreateDirectory(Path.GetDirectoryName(pathFull)!);

        var mcpServers = new JsonObject();

        foreach (var server in injected) {
            var args = new JsonArray();
            foreach (var a in server.Args) args.Add((JsonNode?)a);

            var env = new JsonObject();
            foreach (var e in server.Env) env[e.Name] = (JsonNode?)e.Value;

            mcpServers[server.Name] = new JsonObject {
                ["command"] = (JsonNode?)server.Command,
                ["args"]    = args,
                ["env"]     = env
            };
        }

        var root = new JsonObject { ["mcpServers"] = mcpServers };
        File.WriteAllText(pathFull, root.ToJsonString());
    }

    /// <summary>
    /// Deletes every home in THIS daemon's root whose epoch is not the current one — the crash and
    /// SIGKILL recovery. Safe by construction because the root is not shared.
    /// </summary>
    /// <para><b>Known ordering, accepted:</b> this runs at daemon start, before the orphan-agent
    /// reaper — same accepted residual as <see cref="KiroReviewerHome.SweepStale"/>.</para>
    internal static void SweepStale(string stateDir, string currentEpoch, ILogger? log = null) {
        log ??= NullLogger.Instance;
        var root = RootFor(stateDir);
        if (!Directory.Exists(root)) return;

        var live = $"{Prefix}{Sanitize(currentEpoch)}-";

        IReadOnlyList<string> candidates;

        try {
            candidates = [.. Directory.EnumerateDirectories(root)];
        } catch (Exception ex) {
            log.LogWarning(ex, "Could not enumerate the Antigravity reviewer home root {Root}; skipping the sweep", root);
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
    /// not resolve inside this daemon's root — the same two recursive-delete hazards
    /// <see cref="KiroReviewerHome.Delete"/> guards against, for the same reason: the content is
    /// review context (transcript, diff, findings), not disposable scratch.
    /// </summary>
    internal static void Delete(string homePath, string stateDir, ILogger? log = null) {
        log ??= NullLogger.Instance;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootFor(stateDir)));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(homePath));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            log.LogWarning("Antigravity reviewer home {Path} resolves outside {Root}; refusing to delete", full, root);
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
            log.LogWarning(ex, "Failed to delete Antigravity reviewer home {Path}", full);
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
    /// umask, and the thing that gets written into it is a reviewer's own conversation state. .NET's
    /// mode-carrying overload closes the window at the syscall.</para>
    ///
    /// <para>And not best-effort — same reasoning as <see cref="KiroReviewerHome"/>: the mode IS the
    /// protection here, so a POSIX host that cannot achieve it must not run a reviewer at all.</para>
    /// </summary>
    static void CreateOwnerOnly(string path) {
        if (OperatingSystem.IsWindows()) {
            // Unreachable in production: the Antigravity reviewer capability refuses Windows before
            // any launch. Kept total so a direct call in a test cannot silently create a
            // world-readable directory.
            throw new PlatformNotSupportedException(
                "antigravity_reviewer_unsupported_platform: a reviewer home cannot be created owner-only here.");
        }

        Directory.CreateDirectory(path, OwnerOnly);

        // An existing directory keeps its own mode — CreateDirectory's mode argument applies only when
        // it creates. Verifying covers that, and any filesystem that ignores the request.
        var mode = File.GetUnixFileMode(path);

        if ((mode & (UnixFileMode.GroupRead  | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead  | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException(
                $"antigravity_reviewer_home_not_owner_only: '{path}' is mode {mode}. The reviewer's "
              + "conversation state, and so the review context, would be readable by other users on this host.");
    }

    static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
