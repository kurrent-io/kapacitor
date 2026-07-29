using System.Diagnostics;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Validates <see cref="WorktreeManager.CreateAsync"/> with a baseRef param.
/// We build a real local git repo with two commits on a side ref so we can
/// fetch it back as if it were a PR head and assert the worktree HEAD lines up.
/// </summary>
public class WorktreeManagerTests {
    static (string upstream, string clone) MakeUpstreamWithSideRef(string sideRefName, out string sideCommitSha) {
        var upstream = Path.Combine(Path.GetTempPath(), "kcap-upstream-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(upstream);

        Git(upstream, "init", "-q");
        Git(upstream, "config", "user.email", "test@example.com");
        Git(upstream, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(upstream, "main.txt"), "main");
        Git(upstream, "add", "-A");
        Git(upstream, "commit", "-q", "-m", "initial");

        // Capture the default branch name; git's default has shifted from
        // master to main and varies by user config.
        var defaultBranch = GitCapture(upstream, "branch", "--show-current").Trim();

        // Create a second commit on a detached side branch and store it under
        // a custom ref so the clone can fetch it like a PR head.
        Git(upstream, "checkout", "-q", "-b", "side");
        File.WriteAllText(Path.Combine(upstream, "side.txt"), "side");
        Git(upstream, "add", "-A");
        Git(upstream, "commit", "-q", "-m", "side commit");
        sideCommitSha = GitCapture(upstream, "rev-parse", "HEAD").Trim();
        Git(upstream, "update-ref", sideRefName, sideCommitSha);
        Git(upstream, "checkout", "-q", defaultBranch);
        Git(upstream, "branch", "-D", "side");

        // Allow `git clone` of a non-bare repo over the file:// protocol.
        Git(upstream, "config", "uploadpack.allowAnySHA1InWant", "true");

        var clone = Path.Combine(Path.GetTempPath(), "kcap-clone-" + Guid.NewGuid().ToString("N")[..8]);
        Git(Path.GetTempPath(), "clone", "-q", upstream, clone);

        return (upstream, clone);
    }

    static void Git(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();

        if (proc.ExitCode != 0) {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
        }
    }

    static string GitCapture(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();

        return proc.ExitCode != 0 ? throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}") : proc.StandardOutput.ReadToEnd();
    }

    [Test]
    public async Task CreateAsync_WithBaseRef_WorktreeHeadMatchesFetchedCommit() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/42/head", out var sideSha);

        try {
            var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            var worktree = await manager.CreateAsync(clone, name: "review-pr-42", baseRef: "refs/pull/42/head");

            try {
                var head = GitCapture(worktree.Path, "rev-parse", "HEAD").Trim();

                await Assert.That(head).IsEqualTo(sideSha);
                await Assert.That(worktree.Branch).IsEqualTo("capacitor/review-pr-42");
            } finally {
                await WorktreeManager.RemoveAsync(worktree);
            }
        } finally {
            try { Directory.Delete(upstream, true); } catch {
                /* best-effort */
            }

            try { Directory.Delete(clone, true); } catch {
                /* best-effort */
            }
        }
    }

    [Test]
    public async Task CreateAsync_WithoutBaseRef_StillWorks() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/1/head", out _);

        try {
            var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            var worktree = await manager.CreateAsync(clone);

            try {
                await Assert.That(Directory.Exists(worktree.Path)).IsTrue();
                await Assert.That(worktree.Branch).StartsWith("capacitor/");
            } finally {
                await WorktreeManager.RemoveAsync(worktree);
            }
        } finally {
            try { Directory.Delete(upstream, true); } catch {
                /* best-effort */
            }

            try { Directory.Delete(clone, true); } catch {
                /* best-effort */
            }
        }
    }

    /// <summary>
    /// Concurrent review launches against the same source repo previously
    /// raced on the shared <c>FETCH_HEAD</c> ref — fetch N would land on
    /// <c>FETCH_HEAD</c> after fetch M, then worktree-add for M would create
    /// the wrong commit. The fix routes each fetch into a per-worktree
    /// <c>refs/kcap/review/{name}</c> and worktree-adds from that ref.
    /// This test asserts each worktree HEAD lines up with the SHA we asked
    /// for, even when 5 launches are issued in parallel.
    /// </summary>
    [Test]
    public async Task CreateAsync_ConcurrentBaseRefs_EachWorktreePinnedToCorrectSha() {
        var upstream = Path.Combine(Path.GetTempPath(), "kcap-upstream-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(upstream);

        Git(upstream, "init", "-q");
        Git(upstream, "config", "user.email", "test@example.com");
        Git(upstream, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(upstream, "main.txt"), "main");
        Git(upstream, "add", "-A");
        Git(upstream, "commit", "-q", "-m", "initial");

        var defaultBranch = GitCapture(upstream, "branch", "--show-current").Trim();

        // Build 5 distinct side commits, each saved as its own ref so the clone
        // can fetch them as if they were PR heads.
        const int concurrency = 5;
        var       refs        = new (string RefName, string Sha)[concurrency];

        for (var i = 0; i < concurrency; i++) {
            var refName = $"refs/pull/{100 + i}/head";
            Git(upstream, "checkout", "-q", "-b", $"side-{i}");
            File.WriteAllText(Path.Combine(upstream, $"side-{i}.txt"), $"side-{i}");
            Git(upstream, "add", "-A");
            Git(upstream, "commit", "-q", "-m", $"side {i}");
            var sha = GitCapture(upstream, "rev-parse", "HEAD").Trim();
            Git(upstream, "update-ref", refName, sha);
            Git(upstream, "checkout", "-q", defaultBranch);
            Git(upstream, "branch", "-D", $"side-{i}");
            refs[i] = (refName, sha);
        }

        var clone = Path.Combine(Path.GetTempPath(), "kcap-clone-" + Guid.NewGuid().ToString("N")[..8]);
        Git(Path.GetTempPath(), "clone", "-q", upstream, clone);

        try {
            var manager   = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            var worktrees = new WorktreeInfo[concurrency];

            await Task.WhenAll(
                Enumerable.Range(0, concurrency)
                    .Select(async i => {
                            worktrees[i] = await manager.CreateAsync(clone, name: $"review-{i}", baseRef: refs[i].RefName);
                        }
                    )
            );

            try {
                for (var i = 0; i < concurrency; i++) {
                    var head = GitCapture(worktrees[i].Path, "rev-parse", "HEAD").Trim();
                    await Assert.That(head).IsEqualTo(refs[i].Sha);
                    await Assert.That(worktrees[i].FetchedRef).IsEqualTo($"refs/kcap/review/review-{i}");
                }
            } finally {
                foreach (var w in worktrees) {
                    if (w is not null) await WorktreeManager.RemoveAsync(w);
                }
            }
        } finally {
            try { Directory.Delete(upstream, true); } catch {
                /* best-effort */
            }

            try { Directory.Delete(clone, true); } catch {
                /* best-effort */
            }
        }
    }

    /// <summary>
    /// The fetched ref should be cleaned up by <see cref="WorktreeManager.RemoveAsync"/>
    /// so the source repo doesn't accumulate stale per-worktree refs after
    /// many review launches.
    /// </summary>
    [Test]
    public async Task RemoveAsync_DeletesFetchedRef() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/77/head", out _);

        try {
            var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            var worktree = await manager.CreateAsync(clone, name: "review-77", baseRef: "refs/pull/77/head");

            await Assert.That(worktree.FetchedRef).IsEqualTo("refs/kcap/review/review-77");

            // Sanity: ref exists before cleanup.
            var beforeRefs = GitCapture(clone, "for-each-ref", "refs/kcap/review/").Trim();
            await Assert.That(beforeRefs).Contains("refs/kcap/review/review-77");

            await WorktreeManager.RemoveAsync(worktree);

            var afterRefs = GitCapture(clone, "for-each-ref", "refs/kcap/review/").Trim();
            await Assert.That(afterRefs).IsEmpty();
        } finally {
            try { Directory.Delete(upstream, true); } catch {
                /* best-effort */
            }

            try { Directory.Delete(clone, true); } catch {
                /* best-effort */
            }
        }
    }

    [Test]
    public async Task BorrowedSnapshot_IsIndependent_CopiesDirtyContext_AndRefreshesPristinely() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/88/head", out _);
        var root = Path.Combine(Path.GetTempPath(), "kcap-borrowed-root-" + Guid.NewGuid().ToString("N")[..8]);
        try {
            File.WriteAllText(Path.Combine(clone, "main.txt"), "dirty");
            File.WriteAllText(Path.Combine(clone, "untracked.txt"), "one");
            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);
            var snapshot = await manager.CreateBorrowedSnapshotAsync(clone, "review", CancellationToken.None);
            try {
                await Assert.That(snapshot.IsStandalone).IsTrue();
                await Assert.That(snapshot.Path.StartsWith(clone + Path.DirectorySeparatorChar, StringComparison.Ordinal)).IsFalse();
                await Assert.That(Directory.Exists(Path.Combine(snapshot.Path, ".git"))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(snapshot.Path, ".git"))).IsFalse();
                await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "main.txt"))).IsEqualTo("dirty");
                await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "untracked.txt"))).IsEqualTo("one");
                await Assert.That(GitCapture(clone, "worktree", "list", "--porcelain")).DoesNotContain(snapshot.Path);

                File.WriteAllText(Path.Combine(snapshot.Path, "reviewer-created.txt"), "must disappear");
                File.WriteAllText(Path.Combine(snapshot.Path, ".git", "reviewer-metadata"), "must disappear");
                File.WriteAllText(Path.Combine(clone, "untracked.txt"), "two");
                await manager.SyncFromSourceAsync(clone, snapshot.Path, [], CancellationToken.None);

                await Assert.That(File.Exists(Path.Combine(snapshot.Path, "reviewer-created.txt"))).IsFalse();
                await Assert.That(File.Exists(Path.Combine(snapshot.Path, ".git", "reviewer-metadata"))).IsFalse();
                await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "untracked.txt"))).IsEqualTo("two");
                await Assert.That(File.ReadAllText(Path.Combine(clone, "main.txt"))).IsEqualTo("dirty");
            } finally {
                await WorktreeManager.RemoveAsync(snapshot);
            }
        } finally {
            try { Directory.Delete(upstream, true); } catch { }
            try { Directory.Delete(clone, true); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Test]
    public async Task BorrowedSnapshot_RefreshPreservesRunningExecutionDirectory() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/90/head", out _);
        var root = Path.Combine(Path.GetTempPath(), "kcap-borrowed-root-" + Guid.NewGuid().ToString("N")[..8]);
        Process? holder = null;
        try {
            var sourceCwd = Path.Combine(clone, "src");
            Directory.CreateDirectory(sourceCwd);
            File.WriteAllText(Path.Combine(sourceCwd, "round.txt"), "one");
            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);
            var snapshot = await manager.CreateBorrowedSnapshotAsync(
                clone, sourceCwd, "review-subdir", CancellationToken.None);
            try {
                var psi = new ProcessStartInfo {
                    FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                    WorkingDirectory = snapshot.Path,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (OperatingSystem.IsWindows()) {
                    psi.ArgumentList.Add("/d");
                    psi.ArgumentList.Add("/c");
                    psi.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
                } else {
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("sleep 30");
                }
                holder = Process.Start(psi);
                await Task.Delay(200);

                File.WriteAllText(Path.Combine(sourceCwd, "round.txt"), "two");
                File.WriteAllText(Path.Combine(snapshot.Path, "reviewer-created.txt"), "remove");
                await manager.SyncFromSourceAsync(
                    clone, snapshot.SnapshotRoot!, snapshot.Path, [], CancellationToken.None);

                await Assert.That(holder!.HasExited).IsFalse();
                await Assert.That(Directory.Exists(snapshot.Path)).IsTrue();
                await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "round.txt"))).IsEqualTo("two");
                await Assert.That(File.Exists(Path.Combine(snapshot.Path, "reviewer-created.txt"))).IsFalse();
            } finally {
                if (holder is { HasExited: false }) holder.Kill(entireProcessTree: true);
                holder?.Dispose();
                await WorktreeManager.RemoveAsync(snapshot);
            }
        } finally {
            try { Directory.Delete(upstream, true); } catch { }
            try { Directory.Delete(clone, true); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Test]
    public async Task BorrowedSnapshot_RejectsSymlinkWithoutFollowingIt() {
        Skip.When(OperatingSystem.IsWindows(), "Symlink semantics in this certification are POSIX-only.");
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/89/head", out _);
        var root = Path.Combine(Path.GetTempPath(), "kcap-borrowed-root-" + Guid.NewGuid().ToString("N")[..8]);
        try {
            File.CreateSymbolicLink(Path.Combine(clone, "escape"), Path.Combine(upstream, "main.txt"));
            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await manager.CreateBorrowedSnapshotAsync(clone, "review", CancellationToken.None));
            await Assert.That(ex!.Message).Contains("symlink_unsupported");
        } finally {
            try { Directory.Delete(upstream, true); } catch { }
            try { Directory.Delete(clone, true); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Test]
    public async Task CleanupOrphaned_PreservesActiveBorrowedSnapshotContainerAndRemovesOnlyOrphans() {
        var root = Path.Combine(Path.GetTempPath(), "kcap-cleanup-root-" + Guid.NewGuid().ToString("N")[..8]);
        var activeRoot = Path.Combine(root, "borrowed-snapshots", "active");
        var activeCwd = Path.Combine(activeRoot, "src");
        var orphan = Path.Combine(root, "borrowed-snapshots", "orphan");
        var legacy = Path.Combine(root, "legacy-orphan");
        try {
            Directory.CreateDirectory(activeCwd);
            Directory.CreateDirectory(orphan);
            Directory.CreateDirectory(legacy);
            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);

            await manager.CleanupOrphanedAsync([activeCwd]);

            await Assert.That(Directory.Exists(activeRoot)).IsTrue();
            await Assert.That(Directory.Exists(orphan)).IsFalse();
            await Assert.That(Directory.Exists(legacy)).IsFalse();
        } finally {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>A live launch's vendor state directory survives the sweep, and a dead one does not.
    ///
    /// <para>It holds the reviewer's whole <c>HOME</c> for the launch and is never itself an active
    /// worktree path, so the sweep's plain active-path rule would delete it mid-review — a failure that
    /// would present as an unreproducible vendor crash rather than as anything pointing here.</para></summary>
    [Test]
    public async Task CleanupOrphaned_KeepsALiveVendorStateDirectoryAndRemovesADeadOne() {
        var root       = Path.Combine(Path.GetTempPath(), "kcap-cleanup-state-" + Guid.NewGuid().ToString("N")[..8]);
        var snapshots  = Path.Combine(root, "borrowed-snapshots");
        var activeRoot = Path.Combine(snapshots, "active");
        var activeCwd  = Path.Combine(activeRoot, "src");
        var liveState  = WorktreeManager.VendorStateRootFor(activeRoot);
        var deadState  = WorktreeManager.VendorStateRootFor(Path.Combine(snapshots, "gone"));
        try {
            Directory.CreateDirectory(activeCwd);
            Directory.CreateDirectory(liveState);
            Directory.CreateDirectory(deadState);
            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);

            await manager.CleanupOrphanedAsync([activeCwd]);

            await Assert.That(Directory.Exists(liveState)).IsTrue()
                .Because("the running reviewer's HOME must survive the sweep");
            await Assert.That(Directory.Exists(deadState)).IsFalse()
                .Because("a state directory whose snapshot is gone is an orphan");
        } finally {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>An active SNAPSHOT whose own name ends in the state-directory suffix is not deleted.
    ///
    /// <para>Snapshot directories are named from the agent id, so this shape is reachable. Treating the
    /// suffix as a classification — deriving an "owner" and testing only that — compares a live snapshot
    /// against a path that does not exist and deletes the reviewer's worktree. The suffix is a hint;
    /// the directory's own activeness is always checked first.</para></summary>
    [Test]
    public async Task CleanupOrphaned_KeepsAnActiveSnapshotWhoseNameEndsWithTheStateSuffix() {
        var root       = Path.Combine(Path.GetTempPath(), "kcap-cleanup-collide-" + Guid.NewGuid().ToString("N")[..8]);
        var activeRoot = Path.Combine(root, "borrowed-snapshots", "borrowed-agent" + WorktreeManager.VendorStateSuffix);
        var activeCwd  = Path.Combine(activeRoot, "src");
        try {
            Directory.CreateDirectory(activeCwd);
            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);

            await manager.CleanupOrphanedAsync([activeCwd]);

            await Assert.That(Directory.Exists(activeCwd)).IsTrue()
                .Because("an active snapshot must survive regardless of what its name happens to end with");
        } finally {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
