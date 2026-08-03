using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <paramref name="FetchedRef"/> is the per-worktree ref the daemon fetched
/// the requested <c>baseRef</c> into (e.g. <c>refs/kcap/review/{name}</c>).
/// Tracked so <see cref="WorktreeManager.RemoveAsync"/> can delete it on
/// cleanup. Null for non-review launches.
/// </summary>
public record WorktreeInfo(
        string Path, string Branch, string SourceRepo, bool IsStandalone = false,
        string? FetchedRef = null, string? SnapshotRoot = null) {
    /// <summary>A borrowed cwd (local in-place launch) the daemon does NOT own. Cleanup
    /// never removes it — the <see cref="AgentInstance.Work"/> guard enforces that.</summary>
    public static WorktreeInfo Borrowed(string cwd) => new(cwd, "", cwd, IsStandalone: false);
}

public partial class WorktreeManager(DaemonConfig config, ILogger<WorktreeManager> logger) {
    /// <summary>Excluded from a borrowed snapshot. The vendor MCP config paths are folded in from the one
    /// list, rather than restated: this used to name <c>.mcp.json</c> and <c>.cursor/mcp.json</c> only, so
    /// <c>.kiro/settings/mcp.json</c> — the file measured to get a command executed at session setup —
    /// survived into a launched borrowed snapshot. Two lists of the same thing is how that happened.
    ///
    /// <para><b>Known cost, and it cuts the wrong way.</b> An excluded file is not in the snapshot, so a
    /// borrowed reviewer cannot SEE it — including when the change under review is the file itself. A pull
    /// request that ADDS a hostile <c>.kiro/settings/mcp.json</c> is therefore invisible to the reviewer,
    /// which is exactly the change this exclusion exists to defend against; the reviewer can return clean on
    /// it. The exclusion still holds, because a reviewer that has already executed the payload is worse than
    /// one that missed it, but the gap is real and is tracked separately (a borrowed reviewer cannot see a hostile config the change under review ADDS). Note it predates this change for
    /// the original two paths — folding the list in widened it from two files to eight rather than
    /// introducing it.</para></summary>
    /// <para><b>Lazy, not a field initializer.</b> Static field initializers across PARTIAL FILES have no
    /// useful ordering, and <c>WorkspaceMcpConfigPaths</c> lives in the other partial — as a field this read
    /// it while still <c>default</c>, and spreading a default <c>ImmutableArray</c> threw inside the type
    /// initializer, which would have broken every worktree creation at runtime.</para>
    static string[]? _snapshotExcludedPaths;
    internal static string[] SnapshotExcludedPaths =>
        _snapshotExcludedPaths ??= [".capacitor", ".attached", .. WorkspaceMcpConfigPaths];
    const int MaxSnapshotFiles = 50_000;
    const long MaxSnapshotBytes = 2L * 1024 * 1024 * 1024;
    static StringComparison FileSystemPathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Per-repository gate around the git commands that touch <c>.git/worktrees</c>.
    /// <para>Observed: concurrent launches in one repo can kill a <c>git worktree add</c> with
    /// <c>fatal: failed to read .git/worktrees/&lt;other&gt;/commondir: Success</c>. That is the whole of
    /// the evidence — the error is real and reproduced only on CI. The explanation (a sibling's metadata
    /// being read while another add is still writing it, since <c>worktree add</c> does enumerate the
    /// existing entries while creating its own) is a HYPOTHESIS, not something this change proves; the
    /// nonsensical <c>Success</c> errno is suggestive but not proof. The remedy does not depend on
    /// pinning it down: whatever synchronisation git does here plainly did not prevent this, so
    /// serialising our own concurrent access is correct regardless of which interleaving produced the
    /// message. <c>worktree remove</c> and <c>branch -D</c> also mutate or read the same tree. The
    /// daemon really does run these concurrently (a flow launching several reviewers against one source
    /// repo) — the same concurrency the per-worktree fetch ref below was introduced for.</para>
    /// <para>Static because <see cref="RemoveAsync"/> is static, so the gate is shared across
    /// instances. Growth is one semaphore per distinct repo for process lifetime, accepted rather than
    /// evicted: a daemon sees a handful of repos, and removing an entry while a waiter holds it would
    /// split the gate.</para>
    /// <para><b>Known limits.</b> (1) In-process only — two daemon processes on one repo would still
    /// race; that needs a cross-process file lock. (2) Identity is a canonicalised path, so aliases the
    /// path layer cannot see through — bind mounts, Windows SUBST/mapped drives, 8.3 short names — still
    /// split the gate (safe direction: a missed exclusion, never a wrong merge), and the
    /// case-insensitive comparison can merge <c>Foo</c>/<c>foo</c> on a case-sensitive volume (harmless
    /// over-serialisation). Filesystem identity rather than a path would close both, and .NET exposes
    /// none portably. (3) <c>worktree add</c> runs the repo's <c>post-checkout</c> hook while the gate is
    /// held, so a hook that synchronously asks this daemon for another worktree operation on the SAME
    /// repo would deadlock. No shipped hook does that; the gated calls must stay non-reentrant.</para></summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> WorktreeMetadataGates =
        new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    /// <summary>Runs <paramref name="mutate"/> holding the metadata gate for the repository
    /// <paramref name="repoPath"/> belongs to. Only the metadata-touching git calls belong inside — a
    /// network fetch must stay outside, or concurrent launches serialise behind each other's downloads
    /// for no benefit.
    /// <para>Internal rather than private so the serialisation itself is testable: the race it prevents
    /// is too narrow to reproduce on demand, so the guard is pinned directly instead of through a flaky
    /// end-to-end repro.</para></summary>
    internal static async Task WithWorktreeMetadataGate(string repoPath, Func<Task> mutate) {
        var gate = WorktreeMetadataGates.GetOrAdd(await ResolveGateKeyAsync(repoPath), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try {
            await mutate();
        } finally {
            gate.Release();
        }
    }

    /// <summary>Identity of the metadata being protected: the SHARED git directory, not the checkout
    /// path. A main checkout and each of its linked worktrees have different paths but one common
    /// <c>.git/worktrees</c>, so keying on the path would let a launch from a linked worktree run
    /// unguarded against a launch from the main checkout. <c>rev-parse --git-common-dir</c> is the value
    /// every alias of one repository agrees on.
    /// <para>Best effort: a non-repo path (the standalone-snapshot case) or an old git without
    /// <c>--path-format</c> falls back to the checkout path. Every result goes through
    /// <see cref="NormalizePathKey"/>, which resolves aliased spellings — necessary on the old-git
    /// branch in particular, where a main checkout answers with a RELATIVE <c>.git</c> that has to be
    /// combined with whatever spelling the caller passed.</para></summary>
    /// <remarks>Deliberately NOT cached by checkout path. A cache would have to assume a path's
    /// repository identity never changes, and it can: a linked-worktree directory gets reused for
    /// another repo, a symlink is retargeted, <c>git worktree repair</c> moves a common dir. A stale
    /// entry would then hand two callers on one repository different gates — the failure this whole
    /// change exists to prevent. The cost of not caching is one extra local <c>rev-parse</c> per gated
    /// operation, alongside the <c>worktree</c> command it guards.</remarks>
    static async Task<string> ResolveGateKeyAsync(string repoPath) {
        try {
            // NOT sourceReadOnly: that sets GIT_CONFIG_NOSYSTEM=1, so a repository trusted only through
            // a SYSTEM-scoped safe.directory would fail this probe while the mutation it guards
            // succeeds — silently demoting us to a checkout-path key and re-splitting the very gate this
            // resolution exists to unify. rev-parse is a read, so it needs neither the maintenance
            // suppression nor the lock avoidance that flag also carries.
            //
            // --path-format=absolute needs git 2.31+; on older git this exits non-zero and we resolve
            // the (possibly relative) plain form against the checkout instead.
            var absolute = await RunGitCaptureResult(
                repoPath, GitTimeout, sourceReadOnly: false, "rev-parse", "--path-format=absolute", "--git-common-dir");

            if (absolute.ExitCode == 0 && absolute.Stdout.Trim() is { Length: > 0 } fromAbsolute)
                return NormalizePathKey(fromAbsolute);

            var plain = await RunGitCaptureResult(
                repoPath, GitTimeout, sourceReadOnly: false, "rev-parse", "--git-common-dir");

            if (plain.ExitCode == 0 && plain.Stdout.Trim() is { Length: > 0 } fromPlain)
                return NormalizePathKey(Path.IsPathRooted(fromPlain) ? fromPlain : Path.Combine(repoPath, fromPlain));
        } catch {
            // git missing / timed out / not a repo — fall through to the path-based key.
        }

        return NormalizePathKey(repoPath);
    }

    /// <summary>Makes a gate key out of a directory: absolute, <c>.</c>/<c>..</c> collapsed, trailing
    /// separator dropped, AND each component resolved through symlinks/junctions. The physical
    /// resolution matters because callers do supply aliased spellings — a launch cwd arrives over local
    /// IPC as whatever the client sent, and on macOS <c>/tmp/...</c> and <c>/private/tmp/...</c> are one
    /// directory — and two spellings that produced two keys would split the gate for one repository.
    /// .NET has no realpath, so this walks the components; anything unresolvable (a path that does not
    /// exist yet, a permission error) is left as spelled, which can only split a gate, never merge two
    /// distinct repositories onto one.</summary>
    static string NormalizePathKey(string path) {
        try {
            var current = Path.GetFullPath(path);

            // Re-walk after every substitution: a link's recorded target can itself sit under a
            // symlinked ancestor (on macOS a link to /var/x resolves to /var/x, whose own /var is a
            // link to /private/var), so one pass would leave two spellings of one directory distinct.
            // Bounded so a link cycle terminates instead of spinning.
            for (var hop = 0; hop < 64; hop++) {
                var next = ResolveFirstLinkComponent(current);
                if (next is null) break;
                current = next;
            }

            // Trim a trailing separator so "/repo" and "/repo/" agree — but never down to "", which a
            // POSIX root ("/") or a bare drive root would otherwise become. An empty key is the one
            // dangerous direction: it MERGES every degenerate input onto one gate instead of splitting.
            var trimmed = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return trimmed.Length > 0 ? trimmed : current;
        } catch {
            return path; // too long / invalid chars: worst case a split gate, never a wrong one
        }
    }

    /// <summary>Finds the first component of <paramref name="full"/> that is a symlink/junction and
    /// returns the path with that component replaced by its target; null when no component is a link
    /// (the path is already physical). Components that cannot be inspected — not yet created, no
    /// permission — are treated as not-a-link, so an unresolvable path keeps its spelling.</summary>
    static string? ResolveFirstLinkComponent(string full) {
        var root  = Path.GetPathRoot(full) ?? string.Empty;
        var parts = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var prefix = root;

        for (var i = 0; i < parts.Length; i++) {
            prefix = Path.Combine(prefix, parts[i]);

            FileSystemInfo? target = null;
            try { target = new DirectoryInfo(prefix).ResolveLinkTarget(returnFinalTarget: true); } catch { }

            if (target is null) continue;

            // Re-attach the untouched tail to the target and let the caller walk the result again.
            var rebuilt = target.FullName;
            for (var j = i + 1; j < parts.Length; j++) rebuilt = Path.Combine(rebuilt, parts[j]);

            return Path.GetFullPath(rebuilt);
        }

        return null;
    }

    public async Task<WorktreeInfo> CreateAsync(string repoPath, string? name = null, string? baseRef = null) {
        name ??= $"agent-{Guid.NewGuid():N}"[..20];

        // Place worktrees under the repo's own .capacitor/ directory so they inherit
        // the repo's workspace trust in Claude Code (trust cascades up parent dirs).
        var worktreeRoot = Path.Combine(repoPath, ".capacitor", "worktrees");
        var worktreePath = Path.Combine(worktreeRoot, name);
        var branch       = $"capacitor/{name}";

        Directory.CreateDirectory(worktreeRoot);

        if (await IsGitRepoWithCommits(repoPath)) {
            if (!string.IsNullOrEmpty(baseRef)) {
                // Fetch into a per-worktree ref instead of the shared FETCH_HEAD
                // so concurrent review launches in the same source repo can't
                // race on each other's fetches. The unique ref carries the
                // worktree name so it's traceable and easy to clean up.
                var fetchedRef = $"refs/kcap/review/{name}";
                // Fetch OUTSIDE the metadata gate: it's already collision-free (per-worktree ref) and
                // it's the slow, network-bound half. But a plain fetch ends by running automatic
                // maintenance, whose task list includes worktree-prune — which WOULD touch
                // .git/worktrees, unguarded, while another launch is mid-add. Disable it for this call
                // (both spellings, so the old gc.auto era is covered too). `-c` rather than
                // --no-auto-maintenance: that flag needs a newer git, `-c` works everywhere.
                await RunGit(repoPath, FetchTimeout,
                    "-c", "maintenance.auto=false", "-c", "gc.auto=0",
                    "fetch", "origin", $"{baseRef}:{fetchedRef}");
                await WithWorktreeMetadataGate(repoPath, () =>
                    RunGit(repoPath, GitTimeout, [..NoBranchHooks(), "worktree", "add", "-B", branch, worktreePath, fetchedRef]));
                var fetched = new WorktreeInfo(worktreePath, branch, repoPath, FetchedRef: fetchedRef);
                await StripOrRollBackAsync(fetched);

                return fetched;
            }

            await WithWorktreeMetadataGate(repoPath, () =>
                RunGit(repoPath, GitTimeout, [..NoBranchHooks(), "worktree", "add", worktreePath, "-b", branch]));
            var linked = new WorktreeInfo(worktreePath, branch, repoPath);
            await StripOrRollBackAsync(linked);

            return linked;
        }

        // Standalone: copy files + git init
        Directory.CreateDirectory(worktreePath);
        CopyDirectory(repoPath, worktreePath);
        // BEFORE the initial commit, so the snapshot's own history never carries the hostile config
        // either — a later `git checkout` inside the tree would otherwise restore it.
        StripWorkspaceMcpConfig(worktreePath);
        await RunGit(worktreePath, GitTimeout, [..NoBranchHooks(), "init"]);
        await RunGit(worktreePath, GitTimeout, [..NoBranchHooks(), "add", "-A"]);
        // Identity supplied explicitly. This is the daemon's OWN bookkeeping commit, not the user's work,
        // so it must not depend on the host having git identity configured — a machine without a global
        // user.email fails with "Author identity unknown". Found while CopyDirectory was temporarily
        // repaired and this line became reachable for the first time; that repair was then reverted (see
        // CopyDirectory), so the path still cannot get here — the fix is kept because it is correct and
        // because the repair will land eventually.
        await RunGit(worktreePath, GitTimeout, [
            ..NoBranchHooks(),
            "-c", "user.email=daemon@kcap.local", "-c", "user.name=kcap",
            "commit", "-m", "Initial snapshot"
        ]);

        return new WorktreeInfo(worktreePath, "", repoPath, IsStandalone: true);
    }

    /// <summary>
    /// Git config that must be forced on any git command that checks out or commits branch content.
    ///
    /// <para>A relative <c>core.hooksPath</c> — <c>.githooks</c> is a widespread convention, and a
    /// documented setup step in many repos — makes the hook scripts themselves branch content. Git then runs
    /// the branch's <c>post-checkout</c> during <c>worktree add</c>, i.e. BEFORE anything in this class has
    /// had a chance to neutralize the tree. Review caught it: the MCP strip is pointless if creating the
    /// tree already executed the branch's code. Pointing <c>core.hooksPath</c> at a daemon-owned empty
    /// directory disables the whole mechanism for these commands without touching the operator's config.</para>
    ///
    /// <para><b>This covers the hook-DIRECTORY mechanism only. Do not read it as "no branch-controlled code
    /// runs during checkout."</b> Two sibling vectors are known and deliberately NOT closed here, tracked on
    /// their own issue (see the branch-controlled-execution follow-up): git's config-based hooks (<c>hook.&lt;name&gt;.event</c> / <c>.command</c>), which run
    /// independently of <c>core.hooksPath</c>; and clean/smudge filters, which a branch selects through its
    /// own <c>.gitattributes</c> and which this flag does not affect at all. Both need a rule for "does this
    /// command resolve to branch content" — blunt disabling would break legitimate drivers such as
    /// <c>filter.lfs</c>, so it is real work rather than another <c>-c</c> flag.</para>
    /// </summary>
    /// <summary>
    /// A <c>core.hooksPath</c> that cannot contain a hook, because it cannot be a directory.
    ///
    /// <para>Three earlier revisions of this created an empty directory instead, and every review round
    /// found a new problem with it: a fixed temp name another user could pre-create with a
    /// <c>post-checkout</c>; a mkdir-then-chmod race; a Windows temp location not guaranteed private; an
    /// empty <c>LocalApplicationData</c> silently yielding a RELATIVE path; and one leaked directory per
    /// daemon process. All of that was incidental to needing somewhere with no hooks in it.</para>
    ///
    /// <para><c>/dev/null</c> is not a directory, so git finds no hook there and there is nothing to
    /// create, permission, or clean up — and nothing for another user to squat. Measured: with the
    /// branch's own <c>core.hooksPath</c> a committed <c>post-checkout</c> RUNS during
    /// <c>worktree add</c>; with this it does not, and the worktree is still created normally. Safe on
    /// Windows by construction too — git there either resolves it the same way or treats it as a relative
    /// path that does not exist, and both mean no hook.</para>
    /// </summary>
    internal const string NoHooksPath = "/dev/null";

    static string[] NoBranchHooks() => ["-c", $"core.hooksPath={NoHooksPath}"];

    /// <summary>
    /// Neutralizes the tree, and UNDOES the creation if that fails.
    ///
    /// <para>Neutralization is fail-closed, and it necessarily runs after <c>worktree add</c> has already
    /// registered a worktree, a branch and (for a review launch) a fetched ref. Throwing straight out of
    /// <see cref="CreateAsync"/> means no <see cref="WorktreeInfo"/> ever reaches the caller, so nothing
    /// downstream can clean any of that up and repeated failures accumulate registrations — review caught
    /// it. Rolling back here keeps fail-closed without making it a leak.</para>
    ///
    /// <para>A rollback failure is swallowed in favour of the original exception: the reason the launch is
    /// being refused is more useful to an operator than whatever went wrong while tidying up after it.</para>
    /// </summary>
    async Task StripOrRollBackAsync(WorktreeInfo created) {
        try {
            StripWorkspaceMcpConfig(created.Path);
        } catch {
            try { await RemoveAsync(created); } catch { /* keep the original failure */ }
            throw;
        }
    }

    /// <summary>Removes branch-authored vendor MCP configuration and logs what went, so an operator whose
    /// repo legitimately ships one can tell that kcap removed it rather than that the vendor ignored it.
    /// <para>Called by the worktree creation paths. Borrowed snapshots do not call it — they never
    /// materialise these files in the first place, because <see cref="SnapshotExcludedPaths"/> now folds in
    /// the same list. An earlier version of this comment claimed "every creation path calls this", which
    /// was not true and papered over exactly the gap that left <c>.kiro/settings/mcp.json</c> in a borrowed
    /// snapshot.</para></summary>
    void StripWorkspaceMcpConfig(string worktreePath) {
        var removed = NeutralizeWorkspaceMcpConfig(worktreePath);

        if (removed.Count > 0)
            logger.LogInformation(
                "Removed branch-authored MCP config from agent worktree {Worktree}: {Paths}. These declare "
              + "commands some vendors execute at session start, and a worktree inherits the repo's trust.",
                worktreePath, string.Join(", ", removed));
    }

    /// <summary>Suffix marking a borrowed launch's per-launch vendor state directory, which sits
    /// BESIDE the snapshot it belongs to. Outside the snapshot deliberately: a per-round refresh
    /// replaces the snapshot's contents, which would both destroy the running vendor's state and
    /// present that state to the reviewer as content under review.</summary>
    public const string VendorStateSuffix = ".vendor-state";

    /// <summary>The per-launch vendor state root for a borrowed snapshot. One definition, shared by
    /// the launch path that fills it and the cleanup paths that remove it.</summary>
    public static string VendorStateRootFor(string snapshotRoot) =>
        snapshotRoot.TrimEnd(Path.DirectorySeparatorChar) + VendorStateSuffix;

    public static async Task RemoveAsync(WorktreeInfo worktree, bool deleteBranch = true) {
        if (worktree.IsStandalone) {
            var root = worktree.SnapshotRoot ?? worktree.Path;

            // The vendor state directory is the daemon's, holds the reviewer's whole HOME for this
            // launch, and must not outlive it.
            if (worktree.SnapshotRoot is not null)
                DeleteTreeNoFollow(VendorStateRootFor(worktree.SnapshotRoot));

            DeleteTreeNoFollow(root);

            return;
        }

        // Both of these touch the same metadata tree as `worktree add`: the removal mutates it, and
        // `branch -D` READS it — git refuses to delete a branch checked out in any worktree, so it
        // enumerates them. That read is best-effort here, so a concurrent add could make it fail
        // silently and leak the branch. One gate acquisition covers both. (As with the add, the exact
        // interleaving that breaks is hypothesis; git takes no lock, so we serialise our own access.)
        await WithWorktreeMetadataGate(worktree.SourceRepo, async () => {
            await RunGit(worktree.SourceRepo, GitTimeout, "worktree", "remove", worktree.Path, "--force");

            if (deleteBranch && !string.IsNullOrEmpty(worktree.Branch)) {
                await RunGitBestEffort(worktree.SourceRepo, "branch", "-D", worktree.Branch);
            }
        });

        if (!string.IsNullOrEmpty(worktree.FetchedRef)) {
            await RunGitBestEffort(worktree.SourceRepo, "update-ref", "-d", worktree.FetchedRef);
        }
    }

    /// <summary>Builds an independent, bundle-derived repository snapshot outside the source
    /// checkout. Unlike a linked worktree it shares no gitdir, refs, reflogs, object alternates, or
    /// worktree registration with the requester's repository.</summary>
    public async Task<WorktreeInfo> CreateBorrowedSnapshotAsync(
            string sourceRepoRoot, string? name, CancellationToken ct) {
        return await CreateBorrowedSnapshotAsync(sourceRepoRoot, sourceRepoRoot, name, ct);
    }

    public async Task<WorktreeInfo> CreateBorrowedSnapshotAsync(
            string sourceRepoRoot, string requestedCwd, string? name, CancellationToken ct) {
        var source = Path.GetFullPath(sourceRepoRoot);
        var cwd = Path.GetFullPath(requestedCwd);
        var relativeCwd = Path.GetRelativePath(source, cwd).Replace(Path.DirectorySeparatorChar, '/');
        if (relativeCwd == ".." || relativeCwd.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidOperationException("borrowed_snapshot_cwd_outside_source");
        var root = Path.GetFullPath(Path.Combine(config.WorktreeRoot, "borrowed-snapshots"));
        EnsureSeparateRoots(source, root);
        Directory.CreateDirectory(root);

        name ??= $"borrowed-{Guid.NewGuid():N}"[..25];
        var final = Path.Combine(root, name);
        var staging = final + ".preparing-" + Guid.NewGuid().ToString("N")[..8];
        var promoted = false;
        try {
            await BuildIndependentSnapshotAsync(source, staging, SnapshotExcludedPaths, ct);
            Directory.Move(staging, final);
            promoted = true;
            var executionPath = relativeCwd == "."
                ? final
                : ContainedPath(final, relativeCwd);
            if (!Directory.Exists(executionPath))
                throw new InvalidOperationException("borrowed_snapshot_cwd_missing");
            return new WorktreeInfo(final == executionPath ? final : executionPath, "", source,
                IsStandalone: true, SnapshotRoot: final);
        } catch {
            DeleteTreeNoFollow(staging);
            if (promoted) DeleteTreeNoFollow(final);
            throw;
        }
    }

    /// <summary>Rebuilds a borrowed snapshot from a pristine independent generation, then replaces
    /// the live snapshot contents. The source repository is never used as the reviewer's cwd and
    /// reviewer-created git metadata cannot survive into the next round.</summary>
    public async Task SyncFromSourceAsync(
            string sourceRepoRoot, string targetWorktreePath,
            string[] excludePaths, CancellationToken ct) {
        await SyncFromSourceAsync(
            sourceRepoRoot, targetWorktreePath, targetWorktreePath, excludePaths, ct);
    }

    public async Task SyncFromSourceAsync(
            string sourceRepoRoot, string targetWorktreePath, string executionPath,
            string[] excludePaths, CancellationToken ct) {
        if (string.IsNullOrEmpty(sourceRepoRoot))
            throw new ArgumentException("Source repo root must not be empty.", nameof(sourceRepoRoot));
        if (string.IsNullOrEmpty(targetWorktreePath))
            throw new ArgumentException("Target worktree path must not be empty.", nameof(targetWorktreePath));

        var source = Path.GetFullPath(sourceRepoRoot);
        var target = Path.GetFullPath(targetWorktreePath);
        var execution = Path.GetFullPath(executionPath);
        if (string.Equals(source, target, StringComparison.Ordinal))
            throw new InvalidOperationException($"Source and target paths are the same: {source}");
        if (!Directory.Exists(source))
            throw new InvalidOperationException($"Source repo root does not exist: {source}");
        if (!string.Equals(execution, target, FileSystemPathComparison) &&
            !execution.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                FileSystemPathComparison))
            throw new InvalidOperationException("borrowed_snapshot_execution_path_outside_target");
        if (!File.Exists(Path.Combine(source, ".git")) && !Directory.Exists(Path.Combine(source, ".git")))
            throw new InvalidOperationException($"Source path does not appear to be a git repo (no .git entry): {source}");

        var parent = Directory.GetParent(target)?.FullName
            ?? throw new InvalidOperationException("Snapshot target has no parent directory.");
        var staging = Path.Combine(parent, Path.GetFileName(target) + ".refresh-" + Guid.NewGuid().ToString("N")[..8]);
        try {
            var exclusions = SnapshotExcludedPaths.Concat(excludePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await BuildIndependentSnapshotAsync(source, staging, exclusions, ct);
            ReplaceTreeContentsNoFollow(target, staging, execution);
        } finally {
            DeleteTreeNoFollow(staging);
        }
    }

    async Task BuildIndependentSnapshotAsync(
            string source, string destination, string[] exclusions, CancellationToken ct) {
        for (var attempt = 0; attempt < 2; attempt++) {
            try {
                await BuildIndependentSnapshotOnceAsync(source, destination, exclusions, ct);
                return;
            } catch (SourceChangedException) when (attempt == 0) {
                DeleteTreeNoFollow(destination);
            } catch (SourceChangedException) {
                DeleteTreeNoFollow(destination);
                throw new InvalidOperationException("borrowed_snapshot_source_changed");
            }
        }
        throw new InvalidOperationException("borrowed_snapshot_source_changed");
    }

    async Task BuildIndependentSnapshotOnceAsync(
            string source, string destination, string[] exclusions, CancellationToken ct) {
        var parent = Directory.GetParent(destination)?.FullName
            ?? throw new InvalidOperationException("Snapshot destination has no parent directory.");
        Directory.CreateDirectory(parent);
        var bundle = Path.Combine(parent, ".bundle-" + Guid.NewGuid().ToString("N") + ".git");
        try {
            var sourceHead = (await RunGitCapture(source, GitTimeout, true, "rev-parse", "HEAD")).Trim();
            await RunGit(source, GitTimeout, sourceReadOnly: true, "bundle", "create", bundle, "HEAD");
            if (await GitConfigBoolAsync(source, "core.sparseCheckout"))
                throw new InvalidOperationException("borrowed_snapshot_sparse_checkout_unsupported");

            await RunGit(parent, GitTimeout, "clone", "--no-hardlinks", "--no-checkout", "--", bundle, destination);
            // Same guard as `worktree add`: this checkout materialises branch content, so a relative
            // core.hooksPath would run the branch's post-checkout here too. Missed when the guard was
            // added — it went on the worktree paths only.
            await RunGit(destination, GitTimeout, [..NoBranchHooks(), "checkout", "--detach", "HEAD"]);
            var clonedHead = (await RunGitCapture(destination, GitTimeout, false, "rev-parse", "HEAD")).Trim();
            if (!string.Equals(sourceHead, clonedHead, StringComparison.Ordinal)) throw new SourceChangedException();
            await RunGitBestEffort(destination, "remote", "remove", "origin");
            await RunGitBestEffort(destination, "reflog", "expire", "--expire=now", "--all");
            var fetchHead = Path.Combine(destination, ".git", "FETCH_HEAD");
            if (File.Exists(fetchHead)) File.Delete(fetchHead);

            var staged = await RunGitCapture(source, GitTimeout, true, "ls-files", "--stage", "-z");
            if (staged.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Any(entry => entry.StartsWith("160000 ", StringComparison.Ordinal)))
                throw new InvalidOperationException("borrowed_snapshot_submodules_unsupported");

            var destinationCaseSensitive = IsCaseSensitiveFileSystem(destination);
            var manifest = await ReadSourceManifestAsync(source, exclusions, destinationCaseSensitive, ct);
            await ApplyReservedIndexPolicyAsync(destination,
                manifest.Where(e => e.Value.DestinationRelative is not null).Select(e => e.Key));
            await CopyManifestAsync(source, destination, manifest, ct);
            // Destination names, not source keys — a quarantined entry lands under a different path and
            // would otherwise be swept straight back out as "outside the manifest".
            RemoveFilesOutsideManifest(destination,
                manifest.Select(static e => e.Value.DestinationRelative ?? e.Key), ct);
            VerifyIndependentGit(destination, source);
            await VerifyDestinationManifestAsync(destination, manifest, ct);

            var finalHead = (await RunGitCapture(source, GitTimeout, true, "rev-parse", "HEAD")).Trim();
            var finalManifest = await ReadSourceManifestAsync(source, exclusions, destinationCaseSensitive, ct);
            if (!string.Equals(sourceHead, finalHead, StringComparison.Ordinal) ||
                !ManifestsEqual(manifest, finalManifest))
                throw new SourceChangedException();
            LogSyncCompleted(source, destination, manifest.Count);
        } finally {
            try { if (File.Exists(bundle)) File.Delete(bundle); } catch { /* startup sweep handles leftovers */ }
        }
    }

    static async Task<Dictionary<string, SnapshotFile>> ReadSourceManifestAsync(
            string source, string[] exclusions, bool destinationCaseSensitive, CancellationToken ct) {
        var listing = DecodeNulSeparatedStrictly(
            await RunGitCaptureBytes(source, GitTimeout, true, "ls-files", "-co", "--exclude-standard", "-z"));

        // Quarantine is for BRANCH-authored config — content the reviewer is there to judge. The manifest
        // source lists untracked files too, so without this a developer's local-only MCP config would be
        // copied into a snapshot the reviewer and its model can read: a disclosure the previous
        // drop-everything behaviour did not have. Untracked config is still simply dropped.
        // ORDINAL, and against git's own bytes rather than the normalized key. Case-folding here is a
        // disclosure bug on a case-sensitive filesystem — which is every Linux daemon: an index-tracked
        // `.Cursor/mcp.json` that is absent on disk would admit an untracked, developer-local
        // `.cursor/mcp.json` as "tracked", quarantining private config into a snapshot the reviewer and its
        // model can read. Form C folding has the same shape for a decomposed spelling. Both `ls-files`
        // calls are `-z`, so their paths are byte-identical for the same file and exact matching is right.
        // Case-insensitive comparison stays correct for destination collisions below, where the question is
        // whether two entries can occupy one path on THIS filesystem.
        var tracked = DecodeNulSeparatedStrictly(
                await RunGitCaptureBytes(source, GitTimeout, true, "ls-files", "-z"))
            .ToHashSet(StringComparer.Ordinal);

        var result = new Dictionary<string, SnapshotFile>(StringComparer.OrdinalIgnoreCase);

        // Match the DESTINATION filesystem. Reserving destinations before the tracked-deletion skip (the
        // fix for a real hole) had a cost I first dismissed as pre-existing and was wrong about: a repo
        // tracking both `Foo` and `foo` with one spelling deleted used to snapshot fine, because the absent
        // entry never reached the manifest dictionary. Folded here, it now collides and aborts — so this
        // change WIDENED the limitation rather than inheriting it. Two entries only contend for one path
        // where the filesystem says they do.
        var destinations = new HashSet<string>(
            destinationCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var raw in listing) {
            ct.ThrowIfCancellationRequested();
            // rel IS raw — validated, never rewritten — so the identity the tracked check authorises is the
            // identity that gets opened, hashed, copied and verified.
            var rel = ValidateRelativePath(raw);
            var quarantine = false;

            if (IsUnderExcluded(rel, exclusions)) {
                if (rel.Equals(".attached", StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith(".attached/", StringComparison.OrdinalIgnoreCase) ||
                    rel.Equals(".capacitor", StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith(".capacitor/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"borrowed_snapshot_reserved_path: {rel}");

                // Vendor MCP config is excluded so no vendor EXECUTES it — but a reviewer still has to be
                // able to READ it, and the change under review may BE this file. Dropping it entirely means
                // a pull request that adds a hostile `.kiro/settings/mcp.json` is invisible to the reviewer,
                // which can then return clean on exactly the change the exclusion defends against.
                // Carried under a suffix instead: reviewable content, at a path no vendor looks for.
                // `raw`, not `rel`: the tracked set holds git's exact paths (see above).
                //
                // A tracked file's WORKING-TREE bytes are what get quarantined, including uncommitted local
                // edits — deliberate, and the same contract as every other file here. A borrowed snapshot
                // mirrors the working tree the flow was launched from precisely so a reviewer sees the
                // change as it stands. Tracked vs untracked is the line that matters: tracked config is
                // part of what the reviewer was asked to judge, untracked config is nobody's business.
                if (!IsWorkspaceMcpConfigPath(rel) || !tracked.Contains(raw)) continue;

                quarantine = true;
            }
            // Reserve the destination BEFORE the deletion skip. A path tracked in HEAD but deleted in the
            // working tree still owns its name: if HEAD tracks `.mcp.json.kcap-quarantined` and the working
            // tree deletes it, skipping it first frees the slot for `.mcp.json`'s quarantine copy to take —
            // and the snapshot then shows the reviewer a MODIFIED file where the working tree has a
            // deletion. Reserving first turns that into the refusal it already is when both are present.
            var destination = quarantine ? rel + QuarantineSuffix : rel;

            // Destinations are checked independently of source keys. A repo containing BOTH `.mcp.json`
            // and `.mcp.json.kcap-quarantined` maps two distinct sources onto one destination — one
            // overwrites the other, and identical contents would even pass verification while silently
            // materialising a single file. A hostile branch can add the colliding name deliberately.
            if (!destinations.Add(destination))
                throw new InvalidOperationException($"borrowed_snapshot_path_collision: {destination}");

            var path = ContainedPath(source, rel);
            if (!File.Exists(path)) continue; // tracked deletion
            // EVERY component, not just the leaf. A leaf-only check proves the last name is not a link
            // while `File.Exists`/`FileInfo`/`OpenSequentialRead` happily follow a symlinked PARENT: with a
            // tracked `.cursor/mcp.json` whose `.cursor` has since been replaced by a link to somewhere
            // outside the repo, `ls-files -co` still reports the cached child, so the index spelling looks
            // tracked and branch-authored while the bytes read come from the link's target. Quarantine then
            // publishes them to the reviewer. The tracked check answers a question about the NAME; this
            // answers the one about where the bytes actually come from.
            if (FirstLinkComponent(source, rel) is { } link)
                throw new InvalidOperationException($"borrowed_snapshot_symlink_unsupported: {link}");

            var info = new FileInfo(path);
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"borrowed_snapshot_symlink_unsupported: {rel}");
            if (result.Count >= MaxSnapshotFiles)
                throw new InvalidOperationException("borrowed_snapshot_capacity_exceeded");
            await using var input = OpenSequentialRead(path);
            var streamLength = input.Length;
            total = checked(total + streamLength);
            if (total > MaxSnapshotBytes)
                throw new InvalidOperationException("borrowed_snapshot_capacity_exceeded");
            var prefix = new byte["version https://git-lfs.github.com/spec/v1\n"u8.Length];
            var prefixLength = await input.ReadAsync(prefix, ct);
            if (prefix.AsSpan(0, prefixLength).StartsWith("version https://git-lfs.github.com/spec/v1\n"u8))
                throw new InvalidOperationException($"borrowed_snapshot_lfs_pointer_unsupported: {rel}");
            input.Position = 0;
            var hash = await SHA256.HashDataAsync(input, ct);
            if (input.Length != streamLength) throw new SourceChangedException();
            UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);

            if (!result.TryAdd(rel, new SnapshotFile(streamLength, hash, mode,
                    quarantine ? destination : null)))
                throw new InvalidOperationException($"borrowed_snapshot_path_collision: {rel}");
        }
        return result;
    }

    static async Task CopyManifestAsync(
            string source, string destination, Dictionary<string, SnapshotFile> manifest,
            CancellationToken ct) {
        foreach (var (rel, file) in manifest) {
            ct.ThrowIfCancellationRequested();
            var sourcePath = ContainedPath(source, rel);
            var path = ContainedPath(destination, file.DestinationRelative ?? rel);
            EnsureParentDirectories(destination, path);
            if (Directory.Exists(path)) DeleteTreeNoFollow(path);
            await using (var input = OpenSequentialRead(sourcePath))
            await using (var output = new FileStream(path, new FileStreamOptions {
                Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
                await input.CopyToAsync(output, ct);
            if (file.Mode is { } mode && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
        }
    }

    static void RemoveFilesOutsideManifest(string destination, IEnumerable<string> accepted, CancellationToken ct) {
        // The comparer has to match the FILESYSTEM, and neither constant is right on both.
        //
        // Case-insensitively, a case-only rename keeps the stale spelling: the manifest wants `Foo.txt`,
        // the clone left `foo.txt`, the sweep decides `foo.txt` is wanted, and the snapshot keeps a file
        // the branch renamed — so `git diff` shows the reviewer no rename at all.
        //
        // But plain Ordinal is worse where case does not distinguish files: there, writing `Foo.txt` lands
        // on the same inode as `foo.txt` and enumeration reports the ORIGINAL spelling, so an exact compare
        // would delete the very file the manifest just wrote.
        //
        // So ask the filesystem instead of assuming from the OS — a case-sensitive volume on macOS and a
        // case-insensitive mount on Linux both exist, and `FileSystemPathComparison` gets both wrong.
        var keep = accepted.ToHashSet(
            IsCaseSensitiveFileSystem(destination) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(destination)) {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(entry).Equals(".git", StringComparison.Ordinal)) continue;
            RemoveUnaccepted(entry, destination, keep, ct);
        }
    }

    static bool RemoveUnaccepted(string path, string root, HashSet<string> keep, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReparsePoint)) { File.Delete(path); return true; }
        if (!attrs.HasFlag(FileAttributes.Directory)) {
            if (!keep.Contains(rel)) File.Delete(path);
            return !File.Exists(path);
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) RemoveUnaccepted(child, root, keep, ct);
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        return !Directory.Exists(path);
    }

    static void ReplaceTreeContentsNoFollow(string target, string staging, string executionPath) {
        var relative = Path.GetRelativePath(target, executionPath);
        var protectedSegments = relative == "."
            ? []
            : relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        ReplaceDirectoryContentsNoFollow(target, staging, protectedSegments, 0);
    }

    static void ReplaceDirectoryContentsNoFollow(
            string target, string? staging, string[] protectedSegments, int protectedIndex) {
        var protectedName = protectedIndex < protectedSegments.Length
            ? protectedSegments[protectedIndex]
            : null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(target))
            if (protectedName is null ||
                !Path.GetFileName(entry).Equals(protectedName, FileSystemPathComparison))
                DeleteTreeNoFollow(entry);
        if (staging is not null) foreach (var entry in Directory.EnumerateFileSystemEntries(staging)) {
            if (protectedName is not null &&
                Path.GetFileName(entry).Equals(protectedName, FileSystemPathComparison))
                continue;
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory)) Directory.Move(entry, destination);
            else File.Move(entry, destination);
        }
        if (protectedName is null) return;

        var protectedTarget = Path.Combine(target, protectedName);
        if (File.Exists(protectedTarget)) File.Delete(protectedTarget);
        Directory.CreateDirectory(protectedTarget);
        var protectedStaging = staging is null ? null : Path.Combine(staging, protectedName);
        if (protectedStaging is not null && !Directory.Exists(protectedStaging)) protectedStaging = null;
        ReplaceDirectoryContentsNoFollow(
            protectedTarget, protectedStaging, protectedSegments, protectedIndex + 1);
    }

    static void DeleteTreeNoFollow(string path) {
        // Path.Exists, not File.Exists || Directory.Exists: both of those FOLLOW, so a DANGLING symlink
        // reports absent and this returned early, leaving the link behind. Its parent then failed to
        // delete — and under the fail-closed config strip that turned a branch committing one dangling
        // link into a refusal of every launch.
        if (!Path.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReparsePoint) || !attrs.HasFlag(FileAttributes.Directory)) {
            if (OperatingSystem.IsWindows() && attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteTreeNoFollow(child);
        if (OperatingSystem.IsWindows() && attrs.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        Directory.Delete(path);
    }

    static void EnsureSeparateRoots(string source, string snapshotRoot) {
        var prefix = source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (snapshotRoot.Equals(source, FileSystemPathComparison) ||
            snapshotRoot.StartsWith(prefix, FileSystemPathComparison))
            throw new InvalidOperationException("borrowed_snapshot_root_inside_source");
    }

    /// <summary>
    /// Validates git's path and returns it UNCHANGED. It deliberately rewrites nothing.
    ///
    /// <para>This used to fold `\`→`/`, strip a leading `/`, and apply Form C — and every one of those was
    /// a hole, because the value that passed the tracked-authority check stopped being the value that was
    /// then opened and read. Backslash is a legal filename character on Unix: a branch could track a decoy
    /// named literally <c>.cursor\mcp.json</c>, pass the tracked check on that exact spelling, and have it
    /// rewritten into <c>.cursor/mcp.json</c> — reading and publishing the developer's untracked local
    /// config. Both manifest passes applied the same substitution, so verification agreed.</para>
    ///
    /// <para>git's `-z` output is already repo-relative and forward-slash separated with no quoting, so
    /// there is nothing legitimate to normalize. Anything not of that shape is refused rather than
    /// repaired: an input we would have to rewrite to use is one we do not understand.</para>
    /// </summary>
    static string ValidateRelativePath(string raw) {
        var parts = raw.Split('/');
        if (raw.Length == 0 || raw.StartsWith('/') ||
            parts.Any(p => p is "" or "." or "..") ||
            parts.Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"borrowed_snapshot_invalid_path: {raw}");

        // On Windows the FILESYSTEM performs the substitution this method refuses to: `ContainedPath` maps
        // `/` to the platform separator, and a component carrying a literal `\` — legal on Unix, so a
        // Linux-authored index can hold one — is then resolved by Windows as a directory boundary. The
        // backslash decoy would redirect the read there even though nothing in managed code rewrote it.
        // Such a path cannot be represented faithfully on Windows (git cannot even check it out), so it is
        // refused rather than approximated.
        if (parts.Any(p => p.Contains(Path.DirectorySeparatorChar) ||
                           p.Contains(Path.AltDirectorySeparatorChar)))
            throw new InvalidOperationException($"borrowed_snapshot_invalid_path: {raw}");

        return raw;
    }

    /// <summary>The first component of <paramref name="rel"/> that is a link, walking one component at a
    /// time and NEVER following — mirroring <c>FirstRemovableComponent</c> in the workspace-MCP partial.
    /// Returns null when every component is an ordinary directory or file.</summary>
    static string? FirstLinkComponent(string root, string rel) {
        var current = root;
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            current = Path.Combine(current, part);
            try {
                if (new FileInfo(current).LinkTarget is not null ||
                    new DirectoryInfo(current).LinkTarget is not null)
                    return current;
            } catch {
                // Unreadable: the leaf checks below still apply, and an unreadable path fails on open.
                return null;
            }
            if (!Path.Exists(current)) return null;      // nothing here, nothing below it either
        }
        return null;
    }

    static string ContainedPath(string root, string rel) {
        var path = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, FileSystemPathComparison))
            throw new InvalidOperationException($"borrowed_snapshot_path_escape: {rel}");
        return path;
    }

    static void EnsureParentDirectories(string root, string path) {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"borrowed_snapshot_parent_missing: {path}");
        var stack = new Stack<string>();
        while (!parent.Equals(normalizedRoot, FileSystemPathComparison)) {
            stack.Push(parent);
            parent = Path.GetDirectoryName(parent)
                ?? throw new InvalidOperationException($"borrowed_snapshot_parent_outside_root: {path}");
        }
        while (stack.TryPop(out var dir)) {
            if (File.Exists(dir)) File.Delete(dir);
            Directory.CreateDirectory(dir);
        }
    }

    static async Task VerifyDestinationManifestAsync(
            string destination, Dictionary<string, SnapshotFile> manifest, CancellationToken ct) {
        foreach (var (rel, expected) in manifest) {
            // The DESTINATION name — a quarantined entry is written under a suffix, and verifying the
            // source key would report a mismatch for a file that is exactly where it should be.
            var path = ContainedPath(destination, expected.DestinationRelative ?? rel);
            if (!File.Exists(path) || new FileInfo(path).Length != expected.Length)
                throw new InvalidOperationException($"borrowed_snapshot_destination_mismatch: {rel}");
            await using var input = OpenSequentialRead(path);
            var hash = await SHA256.HashDataAsync(input, ct);
            if (!hash.AsSpan().SequenceEqual(expected.Hash))
                throw new InvalidOperationException($"borrowed_snapshot_destination_mismatch: {rel}");
        }
    }

    static void VerifyIndependentGit(string destination, string source) {
        var gitDir = Path.Combine(destination, ".git");
        if (!Directory.Exists(gitDir) || File.Exists(Path.Combine(gitDir, "objects", "info", "alternates")))
            throw new InvalidOperationException("borrowed_snapshot_git_not_independent");
        foreach (var file in Directory.EnumerateFiles(gitDir, "*", SearchOption.AllDirectories)) {
            if (file.Contains(Path.DirectorySeparatorChar + "objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (new FileInfo(file).Length > 1024 * 1024) continue;
            var text = File.ReadAllText(file);
            if (text.Contains(source, StringComparison.Ordinal))
                throw new InvalidOperationException("borrowed_snapshot_source_path_disclosed");
        }
    }

    static bool ManifestsEqual(
            Dictionary<string, SnapshotFile> left, Dictionary<string, SnapshotFile> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var other) && pair.Value.Mode == other.Mode &&
            pair.Value.Length == other.Length && pair.Value.Hash.AsSpan().SequenceEqual(other.Hash));

    /// <summary>Marks the excluded config paths <c>skip-worktree</c> so their absence from the snapshot is
    /// not reported as a change.
    /// <para>This iterated a hard-coded pair while the snapshot excluded the same pair. Now that the
    /// exclusions fold in <see cref="WorkspaceMcpConfigPaths"/>, a tracked <c>.kiro/settings/mcp.json</c>
    /// would show up as a DELETION inside the snapshot — polluting <c>git status</c> and diffs, and capable
    /// of producing a review finding about a deletion kcap performed. Driven from the same list, so the two
    /// cannot drift apart again.</para></summary>
    static async Task ApplyReservedIndexPolicyAsync(string destination, IEnumerable<string> quarantinedPaths) {
        var quarantined = quarantinedPaths.ToArray();   // enumerated twice below
        // Drive this from the paths the manifest ACTUALLY remapped, in their exact spellings.
        //
        // Two ways to get this wrong, both seen in review. Iterating the canonical lowercase list marks
        // nothing when the index holds `.Cursor/mcp.json`, so kcap's own exclusion surfaces as a DELETION.
        // But classifying every indexed path is worse: IsWorkspaceMcpConfigPath matches on a `/`-suffix, so
        // `fixtures/.mcp.json` qualifies — an ordinary nested file that the root-prefix manifest rules
        // neither exclude nor quarantine. Marking it skip-worktree before its working-tree bytes are copied
        // over hides a REAL staged or unstaged change from `git status` and `git diff`.
        //
        // The manifest already knows exactly which entries were remapped, so ask it instead of re-deriving.
        foreach (var path in quarantined)
            try { await RunGit(destination, GitTimeout, "update-index", "--skip-worktree", "--", path); }
            catch { /* raced away, or the index changed under us */ }
        // EXACT destinations, never `*{suffix}`. A wildcard suppresses every untracked file with that
        // suffix, so a developer's own `fixtures/result.kcap-quarantined` is copied into the snapshot and
        // then vanishes from `git status` — hiding genuine dirty context from the reviewer, the same defect
        // shape as the over-broad skip-worktree. Each pattern is rooted at `/` and escaped, because a
        // branch chooses these path names and gitignore has its own metacharacters.
        static string EscapeExclude(string rel) {
            var escaped = new System.Text.StringBuilder("/");
            foreach (var c in rel) {
                if (c is '*' or '?' or '[' or ']' or '\\' or '!' or '#') escaped.Append('\\');
                escaped.Append(c);
            }
            return escaped.ToString();
        }

        Directory.CreateDirectory(Path.Combine(destination, ".git", "info"));
        File.AppendAllText(Path.Combine(destination, ".git", "info", "exclude"),
            "\n.attached/\n"
          + string.Concat(quarantined.Select(rel => EscapeExclude(rel + QuarantineSuffix) + "\n")));
    }

    /// <summary>Whether <paramref name="root"/> distinguishes filenames by case, PROBED rather than
    /// inferred from the OS. Falls back to case-insensitive — the conservative side, since it keeps rather
    /// than deletes.</summary>
    static bool IsCaseSensitiveFileSystem(string root) {
        // The name must be UNGUESSABLE, and both spellings must be confirmed absent before probing. With a
        // fixed `.kcap-case-probe`, a branch shipping a tracked `.KCAP-CASE-PROBE` makes the upper-case
        // check find ITS file, so a case-sensitive filesystem reports as insensitive, the sweep falls back
        // to folding, and the stale-spelling bug it exists to prevent comes straight back. kcap-cli is
        // public: a literal sentinel is something the branch can simply name its file.
        var stem  = ".kcap-probe-" + Guid.NewGuid().ToString("N");
        var lower = Path.Combine(root, stem);
        var upper = Path.Combine(root, stem.ToUpperInvariant());
        try {
            // If either spelling somehow exists, we cannot attribute the result — say nothing rather than
            // guess. Insensitive is the conservative answer: the sweep then keeps rather than deletes.
            if (File.Exists(lower) || File.Exists(upper) ||
                Directory.Exists(lower) || Directory.Exists(upper)) return false;

            File.WriteAllBytes(lower, []);
            return !File.Exists(upper);
        } catch {
            return false;
        } finally {
            try { File.Delete(lower); } catch { /* nothing to clean up */ }
        }
    }

    static FileStream OpenSequentialRead(string path) => new(path, new FileStreamOptions {
        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });

    /// <param name="DestinationRelative">Where the file lands in the snapshot when that differs from where
    /// it was read. Used only to QUARANTINE vendor MCP config: the content must be reviewable, but not at a
    /// path any vendor reads.</param>
    sealed record SnapshotFile(long Length, byte[] Hash, UnixFileMode? Mode, string? DestinationRelative = null);
    sealed class SourceChangedException : Exception;

    /// <summary>Suffix a quarantined config carries in the snapshot. No vendor looks for these names, so
    /// the content is readable by a reviewer without being loadable by the agent.
    ///
    /// <para><b>Known residual, accepted.</b> A branch <c>.gitignore</c> carrying
    /// <c>!*.kcap-quarantined</c> outranks <c>.git/info/exclude</c>, so the copy can be made to appear in
    /// <c>git status</c> and be staged by a reviewer's <c>git add -A</c>. Nothing placed INSIDE the worktree
    /// can escape branch-controlled ignore rules; only a sidecar outside it could, and a sidecar is
    /// invisible to the reviewer until the flows layer points at it, which defeats the purpose this exists
    /// for. The residual is diff noise in a commit reviewers rarely make, of content already present in the
    /// branch — not execution, which the suffix still prevents. Tracked separately.</para></summary>
    internal const string QuarantineSuffix = ".kcap-quarantined";

    /// <summary>Whether an excluded path is vendor MCP config — the kind that must stay REVIEWABLE — rather
    /// than kcap's own reserved state, which must not appear in a snapshot at all.</summary>
    static bool IsWorkspaceMcpConfigPath(string rel) {
        // Compared as-is. Folding `\` to `/` here classified a top-level file literally named
        // `.cursor\mcp.json` as vendor config, which no vendor would ever read — quarantine would rename an
        // ordinary tracked file and misrepresent the branch to the reviewer. Paths are git paths: `/`
        // separates, and a backslash is just a character in a name.
        return WorkspaceMcpConfigPaths.Any(path =>
            rel.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            rel.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase));
    }

    static bool IsUnderExcluded(string rel, string[] prefixes) {
        // `rel` is git's path and is not rewritten (see ValidateRelativePath); only the caller-supplied
        // prefixes are normalized, since those are our own constants.
        foreach (var prefix in prefixes) {
            var normalized = prefix.Replace('\\', '/').TrimEnd('/');
            if (rel.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public Task CleanupOrphanedAsync(IEnumerable<string>? activeWorktreePaths = null) {
        // Legacy global root — clean up any leftover worktrees from before the per-repo change
        var worktreePaths = activeWorktreePaths as string[] ?? [..activeWorktreePaths ?? []];
        CleanupDirectory(config.WorktreeRoot, worktreePaths, "borrowed-snapshots");
        CleanupDirectory(Path.Combine(config.WorktreeRoot, "borrowed-snapshots"), worktreePaths);

        // Per-repo roots — scan each allowed repo for .capacitor/worktrees/
        foreach (var repoPath in config.AllowedRepoPaths) {
            var cleanPath   = repoPath.TrimEnd('/', '*');
            var perRepoRoot = Path.Combine(cleanPath, ".capacitor", "worktrees");
            CleanupDirectory(perRepoRoot, worktreePaths);
        }

        return Task.CompletedTask;
    }

    void CleanupDirectory(
            string root, IEnumerable<string>? activeWorktreePaths,
            string? reservedDirectoryName = null) {
        if (!Directory.Exists(root)) return;

        var activePaths = activeWorktreePaths?.Select(Path.GetFullPath).ToArray() ?? [];

        foreach (var dir in Directory.GetDirectories(root)) {
            if (reservedDirectoryName is not null &&
                Path.GetFileName(dir).Equals(reservedDirectoryName, StringComparison.OrdinalIgnoreCase))
                continue;
            var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);

            // Two ways to be live, and BOTH are checked. A directory is live if it is itself an active
            // worktree (or contains one) — the original rule — or, for a per-launch vendor state
            // directory, if the snapshot it sits beside is: a state directory is never itself an active
            // worktree path, so without the second arm the sweep reaps a running reviewer's HOME.
            //
            // Checking the first arm unconditionally is what keeps the suffix a hint rather than a
            // classification. Snapshot directories are named from the agent id, so one could legitimately
            // end in the suffix; deriving an "owner" and testing only that would then compare an active
            // snapshot against a path that does not exist and delete the live worktree.
            if (IsActive(fullDir, activePaths)) continue;
            if (fullDir.EndsWith(VendorStateSuffix, StringComparison.OrdinalIgnoreCase) &&
                IsActive(fullDir[..^VendorStateSuffix.Length], activePaths))
                continue;

            LogCleaningUp(dir);
            try { DeleteTreeNoFollow(dir); } catch (Exception ex) { LogCleanupFailed(ex, dir); }
        }
    }

    /// <summary>Whether <paramref name="candidate"/> is, or contains, an active worktree.</summary>
    static bool IsActive(string candidate, string[] activePaths) {
        if (candidate.Length == 0) return false;

        var prefix = candidate + Path.DirectorySeparatorChar;

        return activePaths.Any(path =>
            path.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Default timeout for local git operations (worktree add, init, commit, …).</summary>
    static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Longer timeout for network git operations (fetch).</summary>
    static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long to wait for a timed-out git process to actually die after being killed,
    /// before giving up and unwinding anyway. See the timeout path in
    /// <see cref="RunGitCaptureResult"/> for why the wait matters.</summary>
    static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    static async Task<bool> IsGitRepoWithCommits(string path) {
        try {
            var       psi  = NewGitPsi(path, ["rev-parse", "HEAD"]);
            using var proc = Process.Start(psi)!;
            using var cts  = new CancellationTokenSource(GitTimeout);

            try {
                await proc.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                try { proc.Kill(true); } catch {
                    /* best-effort */
                }

                return false;
            }

            return proc.ExitCode == 0;
        } catch { return false; }
    }

    static Task RunGit(string cwd, TimeSpan timeout, params string[] args) =>
        RunGit(cwd, timeout, sourceReadOnly: false, args);

    static async Task RunGit(string cwd, TimeSpan timeout, bool sourceReadOnly, params string[] args) {
        var result = await RunGitCaptureResult(cwd, timeout, sourceReadOnly, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    static async Task<string> RunGitCapture(string cwd, TimeSpan timeout, bool sourceReadOnly, params string[] args) {
        var result = await RunGitCaptureResult(cwd, timeout, sourceReadOnly, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
        return result.Stdout;
    }

    static async Task<bool> GitConfigBoolAsync(string cwd, string key) {
        var result = await RunGitCaptureResult(cwd, GitTimeout, true, "config", "--bool", "--get", key);
        if (result.ExitCode == 1) return false; // key is absent
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git config --bool --get {key} failed: {result.Stderr}");
        return string.Equals(result.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitCaptureResult(
            string cwd, TimeSpan timeout, bool sourceReadOnly, params string[] args) {
        var psi = NewGitPsi(cwd, args, sourceReadOnly);
        using var proc = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(timeout);

        // Read BYTES and decode here. `StandardOutputEncoding` sets the encoding but does NOT disable BOM
        // detection — .NET builds the redirected reader with detectEncodingFromByteOrderMarks enabled, so a
        // leading EF BB BF is consumed before we see it. Measured: "<BOM>A\0<BOM>B" reads back as
        // "A\0<BOM>B". U+FEFF is a legal character in a git path and `ls-files` output has no global
        // prefix, so a tracked `\uFEFF.cursor/mcp.json` would arrive as `.cursor/mcp.json` in BOTH the
        // tracked set and the manifest — the check passes on a name git does not have, and the read lands
        // on the developer's untracked config. The authorised name must be the used name.
        var stdoutTask = ReadAllDecodedAsync(proc.StandardOutput.BaseStream, cts.Token);
        var stderrTask = ReadAllDecodedAsync(proc.StandardError.BaseStream, cts.Token);
        try {
            await proc.WaitForExitAsync(cts.Token);
        } catch (OperationCanceledException) {
            try { proc.Kill(true); } catch { /* best-effort */ }

            // Kill is asynchronous. Wait for it to land before unwinding: a caller may hold the
            // worktree metadata gate, whose whole point is that no other git touches this repo's
            // metadata while we are inside it — and a killed-but-still-running git would escape it
            // the moment the gate releases. Bounded, so an unkillable process can't wedge us here.
            try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(KillGrace); } catch {
                /* exited, or refused to die within the grace — nothing further we can do */
            }

            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} timed out after {timeout.TotalSeconds:F0}s");
        }
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Splits a NUL-separated git listing and decodes each record STRICTLY.
    ///
    /// <para>A git path is bytes; on Linux a filename need not be valid UTF-8. Such a path must be refused,
    /// because decoded lossily it becomes U+FFFD, no longer names anything, and would be silently skipped
    /// as a tracked deletion — letting a branch hide a tracked file from the reviewer in a snapshot that
    /// exists so the reviewer can see the change.</para>
    ///
    /// <para>But testing the DECODED string for U+FFFD cannot tell an invalid byte from a filename that
    /// legitimately contains U+FFFD (EF BF BD decodes to exactly that), so a valid `notes\uFFFD.md` was
    /// refused too. Deciding on the bytes removes the ambiguity instead of narrowing the guess.</para>
    /// </summary>
    static IEnumerable<string> DecodeNulSeparatedStrictly(byte[] listing) {
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var start = 0;
        for (var i = 0; i <= listing.Length; i++) {
            if (i != listing.Length && listing[i] != 0) continue;
            if (i > start) {
                var record = listing.AsSpan(start, i - start);
                string decoded;
                try { decoded = strict.GetString(record); }
                catch (DecoderFallbackException) {
                    throw new InvalidOperationException(
                        "borrowed_snapshot_invalid_path: a path is not valid UTF-8, so it cannot be "
                      + "represented faithfully in the snapshot");
                }
                yield return decoded;
            }
            start = i + 1;
        }
    }

    /// <summary>Runs git and returns stdout as raw BYTES, for callers that must decide on the bytes.</summary>
    static async Task<byte[]> RunGitCaptureBytes(string cwd, TimeSpan timeout, bool sourceReadOnly,
            params string[] args) {
        var psi = NewGitPsi(cwd, args, sourceReadOnly);
        using var proc = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(timeout);
        using var buffer = new MemoryStream();
        var copy = proc.StandardOutput.BaseStream.CopyToAsync(buffer, cts.Token);
        await proc.WaitForExitAsync(cts.Token);
        await copy;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {proc.ExitCode}");

        return buffer.ToArray();
    }

    /// <summary>Drains a redirected stream and decodes it as UTF-8 with replacement, preserving every byte
    /// the child wrote — including a leading BOM, which <see cref="StreamReader"/> would consume.</summary>
    static async Task<string> ReadAllDecodedAsync(Stream stream, CancellationToken ct) {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return GitOutputEncoding.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>UTF-8 with replacement fallback: an invalid sequence becomes U+FFFD, which is how a path
    /// that cannot round-trip is detected and refused. Never emits or consumes a BOM.</summary>
    static readonly UTF8Encoding GitOutputEncoding =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    static async Task RunGitBestEffort(string cwd, params string[] args) {
        try { await RunGit(cwd, GitTimeout, args); } catch {
            /* best-effort */
        }
    }

    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> for git with prompts disabled
    /// (<c>GIT_TERMINAL_PROMPT=0</c>, <c>GCM_INTERACTIVE=Never</c>) so an
    /// unattended daemon can never block on a credential prompt.
    /// </summary>
    static ProcessStartInfo NewGitPsi(string cwd, string[] args, bool sourceReadOnly = false) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            // Set for any caller using the StreamReader API. The capture path deliberately does NOT use it:
            // it reads BaseStream and decodes with GitOutputEncoding, because this property alone does not
            // disable the reader's BOM detection. See RunGitCaptureResult.
            StandardOutputEncoding = GitOutputEncoding,
            StandardErrorEncoding  = GitOutputEncoding,
            CreateNoWindow         = true,
            Environment = {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"]     = "Never"
            }
        };
        if (sourceReadOnly) {
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            psi.Environment["GIT_CONFIG_COUNT"] = "2";
            psi.Environment["GIT_CONFIG_KEY_0"] = "maintenance.auto";
            psi.Environment["GIT_CONFIG_VALUE_0"] = "false";
            psi.Environment["GIT_CONFIG_KEY_1"] = "core.fsmonitor";
            psi.Environment["GIT_CONFIG_VALUE_1"] = "false";
        }

        return psi;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleaning up orphaned worktree: {Path}")]
    partial void LogCleaningUp(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced {FileCount} files from {Source} into worktree {Target}")]
    partial void LogSyncCompleted(string source, string target, int fileCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to clean up {Path}")]
    partial void LogCleanupFailed(Exception ex, string path);

    /// <summary>
    /// NOTE: this is the ORIGINAL implementation, restored deliberately.
    ///
    /// <para>It is broken: the standalone path's destination is
    /// <c>&lt;source&gt;/.capacitor/worktrees/&lt;name&gt;</c>, so this descends into the directory it is
    /// writing and recurses until the path length blows up. Standalone snapshot creation has therefore
    /// never completed for a non-git source.</para>
    ///
    /// <para><b>Why it is not fixed here.</b> Repairing the recursion made the path REACHABLE, which armed
    /// a second, worse latent bug in the same method: <c>File.Copy</c> copies a symlink's target, so a
    /// source containing a link to <c>~/.ssh</c> would materialise real credentials inside the agent's
    /// worktree. Hardening that then raised further questions about case semantics and about preserving
    /// legitimate internal links. None of it belongs in a change about MCP containment, and shipping a
    /// half-hardened live path is worse than leaving an inert broken one. Repair is tracked on its own
    /// issue, with the exfiltration vector and the case-identity problem written up.</para>
    /// </summary>
    static void CopyDirectory(string source, string dest) {
        foreach (var file in Directory.GetFiles(source)) {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source)) {
            if (Path.GetFileName(dir) == ".git") {
                continue;
            }

            var destDir = Path.Combine(dest, Path.GetFileName(dir));
            Directory.CreateDirectory(destDir);
            CopyDirectory(dir, destDir);
        }
    }
}
