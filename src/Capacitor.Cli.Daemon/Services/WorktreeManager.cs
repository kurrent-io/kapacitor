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
        string? FetchedRef = null, string? SnapshotRoot = null, string? ReviewContextRoot = null) {
    internal BorrowedReviewContextGeneration? ReviewContextGeneration { get; init; }

    /// <summary>The execution cwd as git spells it, relative to the work-tree top; empty at the root.
    /// <para>Computed once at creation and carried, never recomputed. A per-round refresh has only a
    /// TARGET-side execution path, so re-deriving would mean a filesystem-relative derivation — the exact
    /// thing that lets the launch path and the exclusion classifier disagree. Null means "not a borrowed
    /// snapshot"; a refresh that finds it null on one throws rather than falling back.</para>
    /// <para>In-memory is sufficient today because the only refresh caller reads it off the
    /// <c>AgentInstance</c> created at launch, and an <c>AgentInstance</c> does not survive a daemon
    /// restart. If a durable agent registry is ever added, this must be part of what it persists.</para>
    /// </summary>
    public string? GitRelativeCwd { get; init; }

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
    /// <para><b>Superseded by <see cref="SnapshotExclusionPlan"/>.</b> This was a static
    /// <c>[".capacitor", ".attached", ..WorkspaceMcpConfigPaths]</c>, which is only complete when the
    /// reviewer executes at the repository root. It is gone rather than kept alongside the plan: two lists
    /// of the same thing is exactly how <c>.kiro/settings/mcp.json</c> survived into a launched snapshot in
    /// the first place.</para>
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
                repoPath, GitTimeout, sourceReadOnly: false, [],
                "rev-parse", "--path-format=absolute", "--git-common-dir");

            if (absolute.ExitCode == 0 && absolute.Stdout.Trim() is { Length: > 0 } fromAbsolute)
                return NormalizePathKey(fromAbsolute);

            var plain = await RunGitCaptureResult(
                repoPath, GitTimeout, sourceReadOnly: false, [], "rev-parse", "--git-common-dir");

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

        // No filter inventory here on purpose. `worktree add --no-checkout` materialises nothing, so no
        // filter can run during it, and the guarded reset re-inventories inside the TARGET — which is the
        // context that actually decides which drivers load. A source-context inventory would add no
        // containment and could reject a safe launch, since a source-only conditional include can define a
        // driver the target never sees.
        if (await IsGitRepoWithCommits(repoPath)) {
            // Created HERE rather than in a shared prologue. The standalone branch below must validate the
            // destination chain BEFORE anything is created — a pre-existing `.capacitor` or `worktrees`
            // symlink is FOLLOWED by this call, so a check placed after the branch decision would run once
            // the snapshot had already been rooted wherever the source tree chose.
            Directory.CreateDirectory(worktreeRoot);

            var noHooks = NoBranchHooks();
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
                // (both spellings, so the old gc.auto era is covered too). A config override rather than
                // --no-auto-maintenance: that flag needs a newer git than the override transport does.
                await RunGit(repoPath, FetchTimeout,
                    [new("maintenance.auto", "false"), new("gc.auto", "0")],
                    "fetch", "origin", $"{baseRef}:{fetchedRef}");
                await WithWorktreeMetadataGate(repoPath, () =>
                    RunGit(repoPath, GitTimeout, noHooks,
                        "worktree", "add", "--no-checkout", "-B", branch, worktreePath, fetchedRef));
                var fetched = new WorktreeInfo(worktreePath, branch, repoPath, FetchedRef: fetchedRef);
                await StripOrRollBackAsync(fetched);

                return fetched;
            }

            await WithWorktreeMetadataGate(repoPath, () =>
                RunGit(repoPath, GitTimeout, noHooks,
                    "worktree", "add", "--no-checkout", worktreePath, "-b", branch));
            var linked = new WorktreeInfo(worktreePath, branch, repoPath);
            await StripOrRollBackAsync(linked);

            return linked;
        }

        // Standalone: copy files + git init.
        return await CreateStandaloneAsync(repoPath, name, worktreeRoot, worktreePath);
    }

    /// <summary>Validates, claims, and builds a standalone snapshot.
    ///
    /// <para><b>Ordering is the entire point of this method.</b> Every check happens before any write, and
    /// the claim's lifetime strictly encloses rollback. Getting either wrong reopens a race or an escape
    /// that the checks themselves cannot detect.</para>
    /// </summary>
    async Task<WorktreeInfo> CreateStandaloneAsync(
            string repoPath, string name, string worktreeRoot, string worktreePath) {
        // `name` reaches Path.Combine, which DISCARDS the root for an absolute value and would place the
        // destination anywhere; `../evil` would land it outside `worktrees`, where the marker cannot
        // exclude it and the copy recurses into its own destination again. Defense-in-depth today — both
        // real callers omit `name` — but this is a public method.
        if (name.Length == 0 || name is "." or ".." ||
            name != Path.GetFileName(name) || name.AsSpan().ContainsAny('/', '\\'))
            throw new InvalidOperationException($"standalone_snapshot_invalid_name: {name}");

        if (!IsAtOrUnder(ResolveDeepestExisting(worktreePath), ResolveDeepestExisting(repoPath)))
            throw new InvalidOperationException("standalone_snapshot_destination_escape");

        // No-follow, attribute-based, over the components WE introduce below the source root — not from the
        // filesystem root, since a source legitimately reached through a system symlink (macOS /tmp) is
        // normal and must not be refused.
        RefuseIfLink(Path.Combine(repoPath, ".capacitor"));
        RefuseIfLink(worktreeRoot);

        Directory.CreateDirectory(worktreeRoot);

        // Atomic claim. Directory.CreateDirectory is a NO-OP on an existing directory, so the freshness
        // check below is check-then-create and cannot by itself exclude a concurrent SAME-PRINCIPAL caller
        // — both racers would consider the directory theirs, and either rollback would delete the other's
        // snapshot. There is no portable atomic directory claim, but FileMode.CreateNew on a FILE is atomic
        // everywhere, so the claim file supplies the exclusion the directory create cannot.
        var claimPath = Path.Combine(worktreeRoot, ClaimPrefix + name);

        // Test barrier: lets two callers rendezvous in the window where BOTH still see the destination
        // absent. Without it a concurrency test can pass by running the callers sequentially, where the
        // second is refused by the occupied-destination check and the claim's atomicity is never exercised.
        SnapshotPreClaimHook?.Invoke().GetAwaiter().GetResult();

        try {
            using (new FileStream(claimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        } catch (IOException) {
            // Loser path: throws WITHOUT touching the destination. The rollback below is unconditional on
            // worktreePath, so a loser falling through it would delete the WINNER's directory — a claim
            // that made things strictly worse than no claim at all.
            throw new InvalidOperationException($"standalone_snapshot_name_in_use: {name}");
        }

        // The claim is held until all success work is done, or until all rollback has finished, and is
        // released LAST. Releasing it inside BuildStandaloneSnapshotAsync would reopen the very race it
        // closes: that method's own unwinding completes BEFORE the catch below deletes the tree, so a new
        // same-name call could claim, create, and then be deleted by this call's delayed rollback.
        try {
            // Test barrier: holds the winner after the claim FILE exists but before the destination does,
            // so a second caller's acquisition is decided purely by the claim's existence. Without this the
            // handle's own FileShare.None can do the excluding instead, and a weakened FileMode goes
            // undetected.
            SnapshotPostClaimHook?.Invoke().GetAwaiter().GetResult();

            // Absent, not merely "not a link". An existing ordinary directory would be silently adopted:
            // the snapshot would overlay a tree we never created, the rollback would then delete it
            // wholesale, and any repository control data already sitting there escapes the source-side
            // `.git` exclusion entirely — putting `git init`/`commit` back outside the snapshot.
            if (IsPresentEntry(worktreePath))
                throw new InvalidOperationException($"standalone_snapshot_destination_occupied: {name}");

            Directory.CreateDirectory(worktreePath);

            try {
                return await BuildStandaloneSnapshotAsync(repoPath, worktreePath);
            } catch {
                // Only the successful claimant reaches here, so this delete is ownership-gated.
                try { DeleteTreeNoFollow(worktreePath); } catch { /* keep the original failure */ }
                // Test barrier, INSIDE the claim's protected region and after the delete: this is the exact
                // window in which a same-name caller must still be excluded.
                SnapshotRollbackHook?.Invoke().GetAwaiter().GetResult();
                throw;
            }
        } finally {
            // Best effort: a stale claim fails CLOSED, refusing that one name until an operator clears it.
            try { File.Delete(claimPath); } catch { /* the refusal names the file */ }
        }
    }

    internal const string ClaimPrefix = ".kcap-claim-";

    /// <summary>Refuses a destination-chain component that is a link, WITHOUT following it.</summary>
    static void RefuseIfLink(string path) {
        if (IsPresentEntry(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"standalone_snapshot_destination_link: {path}");
    }

    /// <summary>Whether an entry exists at this path, INCLUDING a dangling link.
    ///
    /// <para>Attribute-based rather than <c>Path.Exists</c>, which follows: a dangling link would report
    /// absent and then be created through — the same trap <see cref="DeleteTreeNoFollow"/> documents.</para>
    /// </summary>
    static bool IsPresentEntry(string path) {
        try {
            _ = File.GetAttributes(path);

            return true;
        } catch (FileNotFoundException) {
            return false;
        } catch (DirectoryNotFoundException) {
            return false;
        }
    }

    /// <summary>Test-only injected failure point. The claim-ownership tests need a DETERMINISTIC rollback
    /// window: a wall-clock race would be flaky and, worse, could pass by luck.</summary>
    internal static string? SnapshotFailurePoint;

    /// <summary>Runs inside the claimant's rollback, after the tree is deleted and before the claim is
    /// released — the window a same-name caller must still be excluded from.</summary>
    internal static Func<Task>? SnapshotRollbackHook;

    /// <summary>Runs immediately before the claim is attempted, while the destination is still absent.
    /// Test-only, so two callers can be made to genuinely overlap.</summary>
    internal static Func<Task>? SnapshotPreClaimHook;

    /// <summary>Runs after the claim file exists but before the destination is created. Test-only: lets a
    /// second caller attempt acquisition at the one moment when only the claim's EXISTENCE can exclude it.
    /// </summary>
    internal static Func<Task>? SnapshotPostClaimHook;

    static void FailHereIfRequested(string point) {
        if (SnapshotFailurePoint != point) return;

        throw new InvalidOperationException("injected_standalone_failure");
    }

    async Task<WorktreeInfo> BuildStandaloneSnapshotAsync(string repoPath, string worktreePath) {
        // Unique per invocation and created CreateNew, so a collision is detected rather than silently
        // shared, a hostile source cannot plant one that suppresses real content, and a marker orphaned by
        // a crash can never suppress anything on a later run.
        var markerName = $".kcap-snapshot-exclude-{Guid.NewGuid():N}";
        var markerPath = Path.Combine(Path.GetDirectoryName(worktreePath)!, markerName);

        try {
            // Fail-closed: without the marker the walk recurses into its own destination.
            using (new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            CopySnapshotTree(repoPath, worktreePath, markerName);
            FailHereIfRequested(nameof(CopySnapshotTree));
        } finally {
            // Cleanup failure logs nothing and fails nothing: the snapshot is already built and correct.
            try { File.Delete(markerPath); } catch { /* best effort */ }
        }

        // BEFORE the initial commit, so the snapshot's own history never carries the hostile config
        // either — a later `git checkout` inside the tree would otherwise restore it.
        StripWorkspaceMcpConfig(worktreePath);
        var noHooks = NoBranchHooks();
        await RunGit(worktreePath, GitTimeout, noHooks, "init");
        // Inventoried and logged here, not at the source: `add -A` in this worktree is where the standalone
        // path's filters resolve. Round 6 removed the source-level logging as dead, and this call was
        // applying overrides inline — silently disabling LFS against a README that promises the opposite.
        var standaloneOverrides = await FilterOverridesForAsync(worktreePath);
        await RunGit(worktreePath, GitTimeout, [.. noHooks, .. standaloneOverrides], "add", "-A");
        // Identity supplied explicitly. This is the daemon's OWN bookkeeping commit, not the user's work,
        // so it must not depend on the host having git identity configured — a machine without a global
        // user.email fails with "Author identity unknown". Found while the broken copy was temporarily
        // repaired and this line became reachable for the first time; that repair was reverted then and has
        // landed now, so this is live rather than speculative.
        await RunGit(worktreePath, GitTimeout,
            [.. noHooks, new("user.email", "daemon@kcap.local"), new("user.name", "kcap")],
            "commit", "-m", "Initial snapshot");

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
    /// <para><b>This covers the hook-DIRECTORY mechanism only.</b> Clean/smudge filters are a separate
    /// vector — a branch selects them through its own <c>.gitattributes</c>, and this flag does not affect
    /// them at all — handled by <see cref="BranchFilterOverridesAsync"/>, which disables every defined
    /// driver rather than trying to judge commands. Git's config-based hooks
    /// (<c>hook.&lt;name&gt;.event</c> / <c>.command</c>) are not covered and need not be: measured on git
    /// 2.49, that config is undocumented and does not run.</para>
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

    /// <summary>The hook override. The transport carrying it is proved by the runner that hands it to git
    /// (see <c>ProveConfigTransportIfCarryingAsync</c>), so nothing here has to remember to.</summary>
    static GitConfigOverride[] NoBranchHooks() => [new("core.hooksPath", NoHooksPath)];

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
            // The add above used --no-checkout, so the tree is still empty here. Populating it inside the
            // rollback means a filter-inventory failure cleans up like any other fail-closed step.
            await CheckoutInTargetContextAsync(created.Path);
            StripWorkspaceMcpConfig(created.Path);
        } catch {
            try { await RemoveAsync(created); } catch { /* keep the original failure */ }
            throw;
        }
    }

    /// <summary>
    /// Populates a worktree created with <c>--no-checkout</c>, using an override set enumerated IN that
    /// worktree.
    ///
    /// <para>Inventorying the SOURCE while checking out in the TARGET is a context mismatch: git runs the
    /// checkout in the new worktree, where <c>includeIf "onbranch:capacitor/**"</c> or a gitdir-matching
    /// conditional include can expose a driver the source never reported. Splitting the add from the
    /// checkout is what lets the inventory be taken where the filters actually resolve, and
    /// <c>--no-checkout</c> means nothing is materialised before that guarded step.</para>
    /// </summary>
    async Task CheckoutInTargetContextAsync(string worktreePath) {
        var overrides = await FilterOverridesForAsync(worktreePath);
        var noHooks = NoBranchHooks();

        // `reset --hard HEAD`, not `checkout -- .`: --no-checkout leaves the INDEX unpopulated too, so a
        // pathspec matches nothing. This is the step that materialises the tree, and therefore the step
        // the overrides have to guard.
        await RunGit(worktreePath, GitTimeout, [.. noHooks, .. overrides], "reset", "--hard", "HEAD");
    }

    /// <summary>
    /// The filter overrides for a context, LOGGED as a side effect.
    ///
    /// <para>Single entry point on purpose. Each of the three materialising paths previously called the
    /// inventory directly and remembered — or forgot — to log separately: standalone lost its logging when
    /// a redundant source-level call was deleted, and the borrowed snapshot never had it, so a reviewer on
    /// an LFS host silently got pointer files. Two rounds, one class. Computing and logging together means
    /// a fourth path cannot repeat it.</para>
    /// </summary>
    async Task<GitConfigOverride[]> FilterOverridesForAsync(string gitContextPath) {
        var overrides = await BranchFilterOverridesAsync(gitContextPath);
        LogDisabledFilters(overrides, gitContextPath);

        return overrides;
    }

    /// <summary>Logs which filter drivers were disabled, so an operator whose LFS-tracked file checks out
    /// as pointer text can see why rather than guessing. Silent containment is how a deliberate trade turns
    /// into a bug report.</summary>
    void LogDisabledFilters(GitConfigOverride[] overrides, string repoPath) {
        var drivers = overrides
            .Where(static o => o.Key.StartsWith("filter.", StringComparison.Ordinal)
                            && o.Key.EndsWith(".clean", StringComparison.Ordinal))
            .Select(static o => o.Key["filter.".Length..^".clean".Length])
            .ToArray();

        if (drivers.Length > 0)
            // States WHAT was disabled and why, and stops there. Three callers materialise content three
            // different ways — an owned worktree checks out through git, a standalone snapshot copies
            // source bytes and re-commits with the clean filter off, a borrowed snapshot overwrites the
            // checkout from the source manifest and refuses source-side pointers — so ANY sentence about
            // what the resulting bytes look like is false for at least one of them. Two rounds of review
            // were spent narrowing such a sentence before concluding it should not be here at all: this
            // logging is the whole reason the no-exemption trade is defensible, so it has to be accurate,
            // and per-path behaviour is documented in the README where there is room to be exact.
            logger.LogInformation(
                "Disabled git filter drivers for agent worktree creation from {Repo}: {Drivers}. A branch's "
              + ".gitattributes selects which driver runs, so a driver with a relative command would execute "
              + "branch-supplied code.",
                repoPath, string.Join(", ", drivers));
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

            if (worktree.ReviewContextRoot is not null)
                DeleteTreeNoFollow(worktree.ReviewContextRoot);

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
        // Containment check on the REQUESTED cwd, and nothing else. This value never locates a directory:
        // the execution path below comes from the git-derived prefix, so that the launch and the exclusion
        // classifier cannot be reading two different spellings of the same place.
        // Path.IsPathRooted matters: GetRelativePath returns a ROOTED path across Windows volumes, which
        // neither of the ".." tests catches.
        var requestedRelative = Path.GetRelativePath(source, cwd).Replace(Path.DirectorySeparatorChar, '/');
        if (Path.IsPathRooted(requestedRelative) || requestedRelative == ".." ||
            requestedRelative.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidOperationException("borrowed_snapshot_cwd_outside_source");
        if (!Directory.Exists(cwd))
            throw new InvalidOperationException("borrowed_snapshot_cwd_missing");
        var gitRelativeCwd = await ReadGitRelativeCwdAsync(source, cwd, ct);
        var root = Path.GetFullPath(Path.Combine(config.WorktreeRoot, "borrowed-snapshots"));
        EnsureSeparateRoots(source, root);
        CreateOwnerOnlyDirectory(root);

        name ??= $"borrowed-{Guid.NewGuid():N}"[..25];
        var final = Path.Combine(root, name);
        var staging = final + ".preparing-" + Guid.NewGuid().ToString("N")[..8];
        var reviewContextRoot = ReviewContextRootFor(final);
        var promoted = false;
        try {
            var reviewContextGeneration = await BuildIndependentSnapshotAsync(
                source, staging, gitRelativeCwd, [], reviewContextRoot, ct)
                ?? throw new InvalidOperationException("borrowed_snapshot_review_context_missing");
            Directory.Move(staging, final);
            promoted = true;
            reviewContextGeneration = PublishReviewContextGeneration(
                reviewContextGeneration, reviewContextRoot);
            var executionPath = gitRelativeCwd.Length == 0
                ? final
                : ContainedPath(final, gitRelativeCwd);
            // Created rather than required to exist. Widening the exclusion to the cwd's own directory
            // made a new case reachable: a cwd whose only content IS vendor config now yields no
            // directory at all in the snapshot, and throwing here would refuse the launch for exactly
            // the repositories this change exists to protect. An empty cwd is the truthful result —
            // everything that was there was excluded. Safe to create: RemoveFilesOutsideManifest has
            // already deleted every reparse point, so no component of this path can be a link.
            Directory.CreateDirectory(executionPath);
            return new WorktreeInfo(final == executionPath ? final : executionPath, "", source,
                IsStandalone: true, SnapshotRoot: final, ReviewContextRoot: reviewContextRoot) {
                ReviewContextGeneration = reviewContextGeneration,
                GitRelativeCwd = gitRelativeCwd
            };
        } catch {
            DeleteTreeNoFollow(staging);
            if (promoted) DeleteTreeNoFollow(final);
            DeleteTreeNoFollow(reviewContextRoot);
            throw;
        }
    }

    /// <summary>Rebuilds <paramref name="targetWorktreePath"/> from a pristine independent generation,
    /// then replaces the live contents — the source repository is never used as the reviewer's cwd, and
    /// reviewer-created git metadata cannot survive into the next round. Vendor config is excluded along
    /// the ancestor chain of <paramref name="sourceCwd"/>.
    /// <para><b>Takes a SOURCE cwd, not a target execution path.</b> The overloads this replaces took only
    /// a target-side path, which left no way to obtain the git-derived prefix except by re-deriving it from
    /// the target filesystem — the derivation that lets the launch and the classifier disagree. They had no
    /// production callers, so removing them is cheaper than preserving an unsafe inference. There is no
    /// fallback: a caller that cannot supply a source cwd cannot use this.</para></summary>
    public async Task SyncFromSourceAsync(
            string sourceRepoRoot, string sourceCwd, string targetWorktreePath,
            string[] excludePaths, CancellationToken ct) {
        // The same admission checks CreateBorrowedSnapshotAsync applies to its requested cwd. The
        // work-tree-top check inside ReadGitRelativeCwdAsync already refuses a foreign repository, so
        // these are defence in depth — but they turn "git failed: ..." into a specific coded error, and
        // this overload had no containment check of its own at all.
        var source = Path.GetFullPath(sourceRepoRoot);
        var cwd = Path.GetFullPath(sourceCwd);
        var requestedRelative = Path.GetRelativePath(source, cwd).Replace(Path.DirectorySeparatorChar, '/');
        if (Path.IsPathRooted(requestedRelative) || requestedRelative == ".." ||
            requestedRelative.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidOperationException("borrowed_snapshot_cwd_outside_source");
        if (!Directory.Exists(cwd))
            throw new InvalidOperationException("borrowed_snapshot_cwd_missing");

        var gitRelativeCwd = await ReadGitRelativeCwdAsync(source, cwd, ct);
        _ = await SyncFromSourceCoreAsync(
            sourceRepoRoot, targetWorktreePath, gitRelativeCwd,
            excludePaths, reviewContextRoot: null, ct);
    }

    internal async Task<BorrowedReviewContextGeneration> SyncBorrowedSnapshotFromSourceAsync(
            string sourceRepoRoot, string targetWorktreePath, string gitRelativeCwd,
            string[] excludePaths, string reviewContextRoot, CancellationToken ct) =>
        await SyncFromSourceCoreAsync(
            sourceRepoRoot, targetWorktreePath, gitRelativeCwd,
            excludePaths, reviewContextRoot, ct)
        ?? throw new InvalidOperationException("borrowed_snapshot_review_context_missing");

    async Task<BorrowedReviewContextGeneration?> SyncFromSourceCoreAsync(
            string sourceRepoRoot, string targetWorktreePath, string gitRelativeCwd,
            string[] excludePaths, string? reviewContextRoot, CancellationToken ct) {
        if (string.IsNullOrEmpty(sourceRepoRoot))
            throw new ArgumentException("Source repo root must not be empty.", nameof(sourceRepoRoot));
        if (string.IsNullOrEmpty(targetWorktreePath))
            throw new ArgumentException("Target worktree path must not be empty.", nameof(targetWorktreePath));

        ArgumentNullException.ThrowIfNull(gitRelativeCwd);
        var source = Path.GetFullPath(sourceRepoRoot);
        var target = Path.GetFullPath(targetWorktreePath);
        // Derived from the SAME prefix the exclusion plan is built from — never from the target
        // filesystem. ContainedPath re-checks that it stays inside the target.
        var execution = gitRelativeCwd.Length == 0 ? target : ContainedPath(target, gitRelativeCwd);
        if (string.Equals(source, target, StringComparison.Ordinal))
            throw new InvalidOperationException($"Source and target paths are the same: {source}");
        if (!Directory.Exists(source))
            throw new InvalidOperationException($"Source repo root does not exist: {source}");
        if (!File.Exists(Path.Combine(source, ".git")) && !Directory.Exists(Path.Combine(source, ".git")))
            throw new InvalidOperationException($"Source path does not appear to be a git repo (no .git entry): {source}");

        var parent = Directory.GetParent(target)?.FullName
            ?? throw new InvalidOperationException("Snapshot target has no parent directory.");
        var staging = Path.Combine(parent, Path.GetFileName(target) + ".refresh-" + Guid.NewGuid().ToString("N")[..8]);
        BorrowedReviewContextGeneration? generation = null;
        try {
            generation = await BuildIndependentSnapshotAsync(
                source, staging, gitRelativeCwd, excludePaths, reviewContextRoot, ct);
            ReplaceTreeContentsNoFollow(target, staging, execution);
            if (generation is not null)
                generation = PublishReviewContextGeneration(
                    generation, reviewContextRoot!);
            return generation;
        } catch {
            if (generation is not null) DeleteTreeNoFollow(generation.StoragePath);
            throw;
        } finally {
            DeleteTreeNoFollow(staging);
        }
    }

    async Task<BorrowedReviewContextGeneration?> BuildIndependentSnapshotAsync(
            string source, string destination, string gitRelativeCwd, string[] excludePaths,
            string? reviewContextRoot, CancellationToken ct) {
        for (var attempt = 0; attempt < 2; attempt++) {
            BorrowedReviewContextGeneration? generation = null;
            try {
                // The plan is built INSIDE the attempt, because it depends on the destination's probed
                // case sensitivity and each retry creates a fresh destination. A retry must not reuse the
                // previous attempt's plan.
                generation = await BuildIndependentSnapshotOnceAsync(
                    source, destination, gitRelativeCwd, excludePaths, reviewContextRoot, ct);
                return generation;
            } catch (SourceChangedException) when (attempt == 0) {
                DeleteTreeNoFollow(destination);
                if (generation is not null) DeleteTreeNoFollow(generation.StoragePath);
            } catch (SourceChangedException) {
                DeleteTreeNoFollow(destination);
                if (generation is not null) DeleteTreeNoFollow(generation.StoragePath);
                throw new InvalidOperationException("borrowed_snapshot_source_changed");
            }
        }
        throw new InvalidOperationException("borrowed_snapshot_source_changed");
    }

    async Task<BorrowedReviewContextGeneration?> BuildIndependentSnapshotOnceAsync(
            string source, string destination, string gitRelativeCwd, string[] excludePaths,
            string? reviewContextRoot, CancellationToken ct) {
        var parent = Directory.GetParent(destination)?.FullName
            ?? throw new InvalidOperationException("Snapshot destination has no parent directory.");
        Directory.CreateDirectory(parent);
        var bundle = Path.Combine(parent, ".bundle-" + Guid.NewGuid().ToString("N") + ".git");
        BorrowedReviewContextGeneration? generation = null;
        try {
            var sourceHead = (await RunGitCapture(source, GitTimeout, true, "rev-parse", "HEAD")).Trim();
            var initialIndex = await RunGitCaptureBytes(source, GitTimeout, true, ct,
                "ls-files", "--stage", "-z");
            await RunGit(source, GitTimeout, sourceReadOnly: true, "bundle", "create", bundle, "HEAD");
            if (await GitConfigBoolAsync(source, "core.sparseCheckout"))
                throw new InvalidOperationException("borrowed_snapshot_sparse_checkout_unsupported");

            await RunGit(parent, GitTimeout, "clone", "--no-hardlinks", "--no-checkout", "--", bundle, destination);
            // Same guard as `worktree add`: this checkout materialises branch content, so a relative
            // core.hooksPath would run the branch's post-checkout here too. Missed when the guard was
            // added — it went on the worktree paths only.
            await RunGit(destination, GitTimeout,
                [.. NoBranchHooks(), .. await FilterOverridesForAsync(destination)],
                "checkout", "--detach", "HEAD");
            var clonedHead = (await RunGitCapture(destination, GitTimeout, false, "rev-parse", "HEAD")).Trim();
            if (!string.Equals(sourceHead, clonedHead, StringComparison.Ordinal)) throw new SourceChangedException();
            await RunGitBestEffort(destination, "remote", "remove", "origin");
            await RunGitBestEffort(destination, "reflog", "expire", "--expire=now", "--all");
            var fetchHead = Path.Combine(destination, ".git", "FETCH_HEAD");
            if (File.Exists(fetchHead)) File.Delete(fetchHead);
            // Probed on the ACTUAL destination, never on its parent or the configured worktree root: case
            // behaviour can differ per directory, and a substituted probe would classify under the wrong
            // semantics. Everything downstream reads this one result.
            var caseSensitive = ProbeCaseSensitiveFileSystem(destination);
            var plan = PlanSnapshotExclusions(gitRelativeCwd, caseSensitive, excludePaths);

            if (reviewContextRoot is not null)
                generation = await CreateReviewContextGenerationAsync(
                    source, reviewContextRoot, sourceHead, initialIndex, caseSensitive, plan, ct);

            if (SplitNulRecords(initialIndex)
                .Any(entry => entry.Span.StartsWith("160000 "u8)))
                throw new InvalidOperationException("borrowed_snapshot_submodules_unsupported");

            var manifest = await ReadSourceManifestAsync(source, plan, caseSensitive, ct);
            await ApplyReservedIndexPolicyAsync(destination, plan, caseSensitive, ct);
            await CopyManifestAsync(source, destination, manifest, ct);
            RemoveFilesOutsideManifest(destination, manifest.Keys, caseSensitive, ct);
            VerifyIndependentGit(destination, source);
            await VerifyDestinationManifestAsync(destination, manifest, ct);

            var finalHead = (await RunGitCapture(source, GitTimeout, true, "rev-parse", "HEAD")).Trim();
            var finalIndex = await RunGitCaptureBytes(source, GitTimeout, true, ct,
                "ls-files", "--stage", "-z");
            var finalManifest = await ReadSourceManifestAsync(
                source, plan, caseSensitive, ct);
            if (!string.Equals(sourceHead, finalHead, StringComparison.Ordinal) ||
                !initialIndex.AsSpan().SequenceEqual(finalIndex) ||
                !ManifestsEqual(manifest, finalManifest))
                throw new SourceChangedException();
            LogSyncCompleted(source, destination, manifest.Count);
            return generation;
        } catch {
            if (generation is not null) DeleteTreeNoFollow(generation.StoragePath);
            throw;
        } finally {
            try { if (File.Exists(bundle)) File.Delete(bundle); } catch { /* startup sweep handles leftovers */ }
        }
    }

    static async Task<Dictionary<string, SnapshotFile>> ReadSourceManifestAsync(
            string source, SnapshotExclusionPlan plan, bool caseSensitive, CancellationToken ct) {
        var stdout = await RunGitCaptureBytes(source, GitTimeout, true, ct,
            "ls-files", "-co", "--exclude-standard", "-z");
        // A stage-only addition has no working-tree bytes to mirror. Skip those exact raw paths
        // before decoding so an unrelated, absent non-UTF8 index entry cannot interfere with the
        // review-context extractor's classify-before-decode guarantee.
        var deleted = await RunGitCaptureBytes(source, GitTimeout, true, ct,
            "ls-files", "--deleted", "-z");
        var deletedPaths = SplitNulRecords(deleted)
            .Select(static path => Convert.ToBase64String(path.Span))
            .ToHashSet(StringComparer.Ordinal);
        var comparison = caseSensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var result = new Dictionary<string, SnapshotFile>(comparison);
        long total = 0;
        foreach (var rawBytes in SplitNulRecords(stdout)) {
            ct.ThrowIfCancellationRequested();
            if (deletedPaths.Contains(Convert.ToBase64String(rawBytes.Span))) continue;
            // Vendor config is matched HERE, on the raw bytes, by the same classifier the review-context
            // extractor uses — and before decoding, preserving that extractor's classify-before-decode
            // guarantee. Routing it through IsUnderExcluded instead would be a second matcher with
            // different case-folding semantics (OrdinalIgnoreCase there, ASCII-only here), which is how a
            // path becomes excluded by one and invisible to the other.
            if (ClassifyReservedPath(rawBytes.Span, plan.Reserved, caseSensitive).Kind
                    != ReservedPathMatchKind.Unrelated)
                continue;
            string raw;
            try { raw = StrictUtf8.GetString(rawBytes.Span); }
            catch (DecoderFallbackException ex) {
                throw new InvalidOperationException(
                    "borrowed_snapshot_invalid_path_encoding", ex);
            }
            var rel = NormalizeRelativePath(raw);
            // Only .capacitor, .attached and caller-supplied excludes reach this — ASCII daemon constants.
            if (IsUnderExcluded(rel, plan.SnapshotExclusions, caseSensitive)) {
                var pathComparison = caseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                if (rel.Equals(".attached", pathComparison) ||
                    rel.StartsWith(".attached/", pathComparison) ||
                    rel.Equals(".capacitor", pathComparison) ||
                    rel.StartsWith(".capacitor/", pathComparison))
                    throw new InvalidOperationException($"borrowed_snapshot_reserved_path: {rel}");
                continue;
            }
            var path = ContainedPath(source, rel);
            if (!File.Exists(path)) continue; // tracked deletion
            EnsureNoLinkedComponents(source, path, rel);
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
            if (!result.TryAdd(rel, new SnapshotFile(streamLength, hash, mode)))
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
            var path = ContainedPath(destination, rel);
            EnsureParentDirectories(destination, path);
            EnsureDestinationLeafNoFollow(path);
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

    static void RemoveFilesOutsideManifest(
            string destination, IEnumerable<string> accepted, bool caseSensitive,
            CancellationToken ct) {
        var keep = accepted.ToHashSet(caseSensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Refuses a snapshot root at or under the source checkout.
    /// <para><b>Why it matters beyond tidiness.</b> Claude Code's workspace <c>.mcp.json</c> lookup walks
    /// UPWARD and does not stop at the git root, so the snapshot's physical ancestors are reachable. If the
    /// snapshot landed under the source, the source's own root config would be an ancestor of the
    /// reviewer's cwd and would load — which no amount of excluding inside the snapshot prevents.</para>
    /// <para><b>Lexical AND resolved.</b> The lexical comparison alone is defeated by a
    /// <c>WorktreeRoot</c> configured as a symlink whose target is inside the source: the string test
    /// passes and the snapshot lands there anyway.</para>
    /// <para><b>Residual, accepted and stated.</b> Resolution handles symlinks and Windows junctions. It
    /// does NOT close a Unix bind mount of a source subdirectory at an apparently external path, nor SUBST
    /// or 8.3 aliases. <c>WorktreeRoot</c> is daemon OPERATOR configuration, so reaching those needs an
    /// already-compromised host config rather than branch content — the same alias classes this file's
    /// worktree-metadata gate already documents as defeating a different path-identity check.</para>
    /// </summary>
    static void EnsureSeparateRoots(string source, string snapshotRoot) {
        if (IsAtOrUnder(snapshotRoot, source) ||
            IsAtOrUnder(ResolveDeepestExisting(snapshotRoot), ResolveDeepestExisting(source)))
            throw new InvalidOperationException("borrowed_snapshot_root_inside_source");
    }

    /// <summary>Ancestry over path STRINGS, with both operands normalised to NFC.
    ///
    /// <para><b>Why fold at all.</b> Case folding alone is not enough on a normalisation-insensitive
    /// volume: a typical macOS filesystem treats <c>caf\u00e9</c> composed and decomposed as ONE directory,
    /// while no <c>StringComparison</c> makes those two strings equal. A source spelled one way and a
    /// configured snapshot root spelled the other would otherwise fail both the lexical and the resolved
    /// check and still land the snapshot inside the source.</para>
    ///
    /// <para><b>Why unconditionally, and what it costs.</b> On a normalisation-SENSITIVE volume those are
    /// genuinely distinct directories, so folding can refuse a layout that is actually fine. The refusal
    /// needs an operator to have spelled the source and the worktree root with different normalisations of
    /// the same name, on such a volume, and it fails closed with a specific coded error — so the cost is a
    /// clear error in a vanishingly rare configuration, against a containment bypass in a common one.</para>
    ///
    /// <para>A probe was written to make this conditional and then REMOVED. Deciding by probe meant
    /// creating a file inside the user's own checkout — which the source manifest reads as untracked
    /// content — and deleting a second pathname the probe had not created; and its lookup used
    /// <c>File.Exists</c>, which reports <c>false</c> for access and I/O errors as well as for absence, so
    /// a failed probe read as "normalisation-sensitive" and silently reopened the bypass. Fail-open plus a
    /// destructive cleanup is a worse trade than an over-refusal.</para>
    ///
    /// <para>True filesystem identity would settle it, but .NET exposes no portable device/inode pair, so
    /// exotic aliases stay in the trusted-configuration residual documented on
    /// <see cref="EnsureSeparateRoots"/>.</para></summary>
    static bool IsAtOrUnder(string candidate, string root) {
        candidate = candidate.Normalize(NormalizationForm.FormC);
        root = root.Normalize(NormalizationForm.FormC);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.Equals(root, FileSystemPathComparison) ||
               candidate.StartsWith(prefix, FileSystemPathComparison);
    }

    /// <summary>Resolves links along the whole existing prefix of <paramref name="path"/>, then appends
    /// whatever does not exist yet literally.
    /// <para>The snapshot root is created by the very call that checks it, so "resolve the final path" is
    /// undefined. Appending the tail without re-resolving also means a component substituted after the
    /// check cannot be followed by this function.</para>
    /// <para><b>Every component, not just the deepest.</b> An earlier version tested <c>LinkTarget</c> on
    /// the deepest existing component alone, so with <c>/alias -> /real</c> and an ordinary
    /// <c>/alias/existing</c>, resolving <c>/alias/existing/new</c> returned the lexical path and the
    /// containment check still missed a snapshot root reaching inside the source through the ancestor
    /// link. Chains are followed with a bounded iteration count rather than trusted to terminate.</para>
    /// </summary>
    static string ResolveDeepestExisting(string path) {
        const int maxLinkHops = 64;
        var full = Path.GetFullPath(path);
        var tail = new List<string>();
        var current = full;

        // Split off the components that do not exist yet; they are re-appended verbatim below.
        while (!Path.Exists(current)) {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) return full;
            tail.Add(Path.GetFileName(current));
            current = parent;
        }
        tail.Reverse();

        // Walk the existing prefix component by component, resolving each link as it is encountered so an
        // ancestor link is followed rather than skipped.
        var resolved = Path.GetPathRoot(current) ?? "";
        var components = current[resolved.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var component in components) {
            resolved = Path.Combine(resolved, component);
            // ResolveLinkTarget(returnFinalTarget: true) already follows a chain, so this loop is
            // belt-and-braces for a target that is itself a link relative to a different parent. It fails
            // CLOSED on exhaustion rather than continuing with a half-resolved path — silently carrying on
            // is how a containment check ends up comparing something that is not the real location.
            var hop = 0;
            for (; hop < maxLinkHops; hop++) {
                var target = new DirectoryInfo(resolved).LinkTarget is not null
                        || new FileInfo(resolved).LinkTarget is not null
                    ? new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                      ?? new FileInfo(resolved).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    : null;
                if (target is null) break;
                resolved = Path.GetFullPath(target);
            }
            if (hop == maxLinkHops)
                throw new InvalidOperationException("borrowed_snapshot_path_link_chain_too_deep");
        }

        return tail.Count == 0 ? resolved : Path.Combine([resolved, .. tail]);
    }

    internal static string NormalizeRelativePath(string raw) {
        if (raw.Length == 0 || raw.StartsWith('/') || raw.Contains('\\') ||
            raw.Contains('\r') || raw.Contains('\n') ||
            !raw.IsNormalized(NormalizationForm.FormC) ||
            raw.Split('/').Any(p => p is "" or "." or "..") ||
            raw.Split('/').Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"borrowed_snapshot_invalid_path: {raw}");
        return raw;
    }

    static void EnsureNoLinkedComponents(string root, string path, string rel) {
        var current = Path.GetFullPath(root);
        foreach (var component in rel.Split('/')) {
            current = Path.Combine(current, component);
            if (!Path.Exists(current)) return;
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException(
                    $"borrowed_snapshot_symlink_unsupported: {rel}");
        }
    }

    static string ContainedPath(string root, string rel) {
        var path = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, FileSystemPathComparison))
            throw new InvalidOperationException($"borrowed_snapshot_path_escape: {rel}");
        return path;
    }

    internal static void EnsureParentDirectories(string root, string path) {
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
            if (Path.Exists(dir)) {
                var attributes = File.GetAttributes(dir);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException(
                        $"borrowed_snapshot_destination_symlink_unsupported: {dir}");
                if (!attributes.HasFlag(FileAttributes.Directory)) File.Delete(dir);
            }
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var created = File.GetAttributes(dir);
            if (created.HasFlag(FileAttributes.ReparsePoint) ||
                !created.HasFlag(FileAttributes.Directory))
                throw new InvalidOperationException(
                    $"borrowed_snapshot_destination_symlink_unsupported: {dir}");
        }
    }

    internal static void EnsureDestinationLeafNoFollow(string path) {
        if (!Path.Exists(path)) return;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException(
                $"borrowed_snapshot_destination_symlink_unsupported: {path}");
    }

    static async Task VerifyDestinationManifestAsync(
            string destination, Dictionary<string, SnapshotFile> manifest, CancellationToken ct) {
        foreach (var (rel, expected) in manifest) {
            var path = ContainedPath(destination, rel);
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
    /// not reported as a change — otherwise a tracked <c>src/.mcp.json</c> shows up as a DELETION in the
    /// reviewer's <c>git status</c> and diff, and can produce a review finding about a deletion kcap
    /// performed.
    ///
    /// <para><b>Reads the DESTINATION index, not the source's.</b> The destination is a fresh clone checked
    /// out at <c>HEAD</c>, so a path staged-but-uncommitted in the source is in the SOURCE index and absent
    /// here. Intersecting against the source listing would batch such a path and fail
    /// <c>update-index</c> on a perfectly legitimate snapshot.</para>
    ///
    /// <para><b>Membership decides, so failure is real.</b> The previous version swallowed every error to
    /// cover "absent from the index". With membership established from the listing, a failure means
    /// something else went wrong and it propagates.</para>
    ///
    /// <para><b>Batched on stdin.</b> The expanded set is <c>paths × depth</c> entries and O(depth²)
    /// aggregate bytes, so argv could exceed <c>ARG_MAX</c>; <c>--stdin</c> also makes the paths literal,
    /// where an ancestor directory named <c>:foo</c> or <c>-foo</c> would otherwise be read as pathspec
    /// syntax.</para>
    ///
    /// <para>The targets are the index's OWN spellings, taken from the listing, so they are guaranteed to
    /// name entries git will accept — and the case decision is made once, by the shared classifier.</para>
    /// </summary>
    static async Task ApplyReservedIndexPolicyAsync(
            string destination, SnapshotExclusionPlan plan, bool caseSensitive, CancellationToken ct) {
        var indexListing = await RunGitCaptureBytes(destination, GitTimeout, false, ct, "ls-files", "-z");
        var targets = new List<string>();
        foreach (var record in SplitNulRecords(indexListing)) {
            ct.ThrowIfCancellationRequested();
            // Every non-Unrelated match, Exact AND Descendant — the same set ReadSourceManifestAsync
            // excludes. An earlier version marked only Exact on the reasoning that a descendant of a
            // config path is not an index entry, which is backwards: the reserved parent may not be an
            // entry, but a repository CAN track `.codex/config.toml/child` (the config pathname as a
            // directory), and each such child is a real index entry. Omitted from the snapshot and left
            // unmarked, it reads as a deletion — and an ordinary git operation in the snapshot could
            // restore it and rebuild a live vendor-config tree.
            if (ClassifyReservedPath(record.Span, plan.Reserved, caseSensitive).Kind
                    == ReservedPathMatchKind.Unrelated)
                continue;
            // A non-UTF8 index path cannot have matched an ASCII candidate, so this cannot throw for a
            // path that reached here; the guard is for the impossible case rather than a silent skip.
            targets.Add(StrictUtf8.GetString(record.Span));
        }

        if (targets.Count > 0)
            await RunGitWithNulStdinAsync(
                destination, GitTimeout, targets, ct, "update-index", "--skip-worktree", "-z", "--stdin");

        Directory.CreateDirectory(Path.Combine(destination, ".git", "info"));
        File.AppendAllText(Path.Combine(destination, ".git", "info", "exclude"), "\n.attached/\n");
    }

    static FileStream OpenSequentialRead(string path) => new(path, new FileStreamOptions {
        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });

    sealed record SnapshotFile(long Length, byte[] Hash, UnixFileMode? Mode);
    sealed class SourceChangedException : Exception;

    static bool IsUnderExcluded(string rel, string[] prefixes, bool caseSensitive) {
        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        foreach (var prefix in prefixes) {
            var normalized = prefix.TrimEnd('/');
            if (rel.Equals(normalized, comparison) ||
                rel.StartsWith(normalized + "/", comparison))
                return true;
        }
        return false;
    }

    public Task CleanupOrphanedAsync(IEnumerable<string>? activeWorktreePaths = null) {
        // Legacy global root — clean up any leftover worktrees from before the per-repo change
        var worktreePaths = activeWorktreePaths as string[] ?? [..activeWorktreePaths ?? []];
        CleanupDirectory(config.WorktreeRoot, worktreePaths, "borrowed-snapshots");
        var borrowedSnapshotsRoot = Path.Combine(config.WorktreeRoot, "borrowed-snapshots");
        // This name is a daemon-owned routing entry. Never enumerate through a pre-existing link:
        // doing so would turn orphan cleanup into deletion of directories outside WorktreeRoot.
        // Removing the link itself is safe and leaves its target untouched.
        if (Path.Exists(borrowedSnapshotsRoot) &&
            File.GetAttributes(borrowedSnapshotsRoot).HasFlag(FileAttributes.ReparsePoint)) {
            LogCleaningUp(borrowedSnapshotsRoot);
            try { DeleteTreeNoFollow(borrowedSnapshotsRoot); }
            catch (Exception ex) { LogCleanupFailed(ex, borrowedSnapshotsRoot); }
        } else {
            CleanupDirectory(borrowedSnapshotsRoot, worktreePaths);
        }

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
            if (fullDir.EndsWith(ReviewContextSuffix, StringComparison.OrdinalIgnoreCase) &&
                IsActive(fullDir[..^ReviewContextSuffix.Length], activePaths))
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
        RunGit(cwd, timeout, sourceReadOnly: false, [], args);

    static Task RunGit(string cwd, TimeSpan timeout, bool sourceReadOnly, params string[] args) =>
        RunGit(cwd, timeout, sourceReadOnly, [], args);

    static Task RunGit(string cwd, TimeSpan timeout, GitConfigOverride[] config, params string[] args) =>
        RunGit(cwd, timeout, sourceReadOnly: false, config, args);

    static async Task RunGit(
            string cwd, TimeSpan timeout, bool sourceReadOnly, GitConfigOverride[] config,
            params string[] args) {
        var result = await RunGitCaptureResult(cwd, timeout, sourceReadOnly, config, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    static async Task<string> RunGitCapture(string cwd, TimeSpan timeout, bool sourceReadOnly, params string[] args) {
        var result = await RunGitCaptureResult(cwd, timeout, sourceReadOnly, [], args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
        return result.Stdout;
    }

    static async Task<bool> GitConfigBoolAsync(string cwd, string key) {
        var result = await RunGitCaptureResult(cwd, GitTimeout, true, [], "config", "--bool", "--get", key);
        if (result.ExitCode == 1) return false; // key is absent
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git config --bool --get {key} failed: {result.Stderr}");
        return string.Equals(result.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitCaptureResult(
            string cwd, TimeSpan timeout, bool sourceReadOnly, GitConfigOverride[] config,
            params string[] args) {
        await ProveConfigTransportIfCarryingAsync(cwd, sourceReadOnly, config);

        return await RunGitCaptureResultUnproven(cwd, timeout, sourceReadOnly, config, args);
    }

    /// <summary>
    /// The proof gate, applied where the overrides are HANDED TO GIT rather than where they are composed.
    ///
    /// <para>An earlier revision proved the transport inside each producer of containment config instead.
    /// Review found the hole that shape leaves: <c>ReadGitRelativeCwdAsync</c> passes
    /// <c>core.quotePath=false</c> and is not a containment producer, so it carried an override past no
    /// proof at all — a fourth call site would have had the same problem, and a fifth. Gating on "this run
    /// carries overrides" cannot be forgotten by a new caller.</para>
    ///
    /// <para><b>Including <c>sourceReadOnly</c>'s own pair.</b> A revision of this gated only on
    /// caller-supplied config, reasoning that losing that flag's suppressions costs a maintenance run.
    /// Review corrected it: <c>core.fsmonitor</c> set to anything other than a boolean names a HOOK
    /// PROGRAM, which git runs whenever a command refreshes the index, so silently dropping
    /// <c>core.fsmonitor=false</c> re-exposes an execution surface — not a performance one. There is no
    /// per-key classification here for the same reason there is no filter-driver exemption: the argument
    /// for "this one is harmless to lose" is exactly what keeps turning out to be wrong. Every git run
    /// carrying ANY override proves the transport first. The cost is that a git too old to honour the
    /// transport cannot be used for these reads either — that git already cannot create a worktree.</para>
    /// </summary>
    static Task ProveConfigTransportIfCarryingAsync(
            string cwd, bool sourceReadOnly, GitConfigOverride[] config) =>
        sourceReadOnly || config.Length > 0 ? ProveConfigTransportAsync(cwd) : Task.CompletedTask;

    /// <summary>Runs git WITHOUT proving the transport first. Only the probe itself may use this — it is
    /// what the gate above is measuring, so routing it through the gate would deadlock on the
    /// non-reentrant semaphore.</summary>
    static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitCaptureResultUnproven(
            string cwd, TimeSpan timeout, bool sourceReadOnly, GitConfigOverride[] config,
            params string[] args) {
        var psi = NewGitPsi(cwd, args, sourceReadOnly, config);
        using var proc = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(timeout);

        // Read BYTES and decode them here. `StandardOutputEncoding` sets the encoding but does NOT turn
        // off BOM detection: .NET builds the redirected reader with detectEncodingFromByteOrderMarks
        // enabled, so a leading EF BB BF is swallowed before we ever see it. Measured — feeding
        // "<BOM>A\0<BOM>B" through the reader yields "A\0<BOM>B": the FIRST BOM vanishes, later ones
        // survive. U+FEFF is a legal character in a git path and in a config subsection, so that silently
        // rewrites the first record of a `-z` listing — the authorised name stops being the used name,
        // which is the whole failure class these guards exist to prevent.
        //
        // Measured reachability, so the claim is not broader than the evidence: for `config --list` the
        // first records come from system/global config, which a branch does not control, so the filter
        // inventory cannot be reached this way today. `ls-files` has no such prefix and IS reachable —
        // that is where the regression test lives. The fix is here because the helper is shared and the
        // property should not depend on which caller happens to be safe.
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

    /// <summary>Drains a redirected stream and decodes it as UTF-8 with replacement, preserving every
    /// byte the child wrote — including a leading BOM, which <see cref="StreamReader"/> would consume.</summary>
    static async Task<string> ReadAllDecodedAsync(Stream stream, CancellationToken ct) {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return GitOutputEncoding.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>UTF-8 with replacement fallback: an invalid sequence becomes U+FFFD, which is how a name
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
    static ProcessStartInfo NewGitPsi(
            string cwd, string[] args, bool sourceReadOnly = false, GitConfigOverride[]? config = null) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            // Set for any caller that uses the StreamReader API. The capture path deliberately does NOT:
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
        }

        // Every override this process passes goes through ONE composition, so the indices and the count are
        // allocated in a single place. An earlier revision wrote the sourceReadOnly pair at fixed indices
        // 0 and 1 with a fixed count of 2, which both displaced any inherited entry and left no room for a
        // caller's own overrides.
        GitConfigOverride[] composed = sourceReadOnly
            ? [.. SourceReadOnlyConfig, .. config ?? []]
            : config ?? [];
        ApplyConfigOverrides(psi.Environment, composed);

        return psi;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleaning up orphaned worktree: {Path}")]
    partial void LogCleaningUp(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced {FileCount} files from {Source} into worktree {Target}")]
    partial void LogSyncCompleted(string source, string target, int fileCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to clean up {Path}")]
    partial void LogCleanupFailed(Exception ex, string path);

}
