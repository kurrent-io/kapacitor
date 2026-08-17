using System.Diagnostics;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

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
        // Repository-local identity is part of the fixture: several snapshot tests add commits in
        // the clone, and CI intentionally has no global Git author configured. Without this the
        // tests pass only on developer machines whose personal config happens to fill the gap.
        Git(clone, "config", "user.email", "test@example.com");
        Git(clone, "config", "user.name", "Test");

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
        using var tmp = new TempDir();
        var upstream = tmp.CreateDir("upstream");

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

        var clone = tmp.CreateDir("clone");
        Git(Path.GetTempPath(), "clone", "-q", upstream, clone);

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

    /// <summary>The submodule arrives as plain content — dirty and untracked files included, so this
    /// cannot pass against a pinned-commit checkout — and with no .git of its own.</summary>
    [Test]
    public async Task BorrowedSnapshot_CarriesSubmoduleFilesAsPlainContentWithoutItsGit() {
        var (subUpstream, sub)     = MakeUpstreamWithSideRef("refs/pull/91/head", out _);
        var (superUpstream, super) = MakeUpstreamWithSideRef("refs/pull/92/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
        try {
            File.WriteAllText(Path.Combine(sub, "lib.txt"), "sub-tracked");
            Git(sub, "add", "lib.txt");
            Git(sub, "commit", "-m", "sub content");

            Git(super, "-c", "protocol.file.allow=always", "submodule", "add", sub, "vendored");
            Git(super, "commit", "-m", "add submodule");

            // Dirty + untracked inside the submodule: the whole point of borrowing is that the
            // reviewer sees the developer's actual working tree, not the pinned commit.
            File.WriteAllText(Path.Combine(super, "vendored", "lib.txt"), "sub-dirty");
            File.WriteAllText(Path.Combine(super, "vendored", "scratch.txt"), "sub-untracked");

            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);
            var snapshot = await manager.CreateBorrowedSnapshotAsync(super, "review", CancellationToken.None);

            try {
                await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "vendored", "lib.txt")))
                    .IsEqualTo("sub-dirty");
                await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "vendored", "scratch.txt")))
                    .IsEqualTo("sub-untracked");
                // No git identity for the submodule inside the snapshot, in either shape.
                await Assert.That(File.Exists(Path.Combine(snapshot.Path, "vendored", ".git"))).IsFalse();
                await Assert.That(Directory.Exists(Path.Combine(snapshot.Path, "vendored", ".git"))).IsFalse();
                // The superproject's own snapshot .git is still the independent one.
                await Assert.That(Directory.Exists(Path.Combine(snapshot.Path, ".git"))).IsTrue();
            } finally {
                await WorktreeManager.RemoveAsync(snapshot);
            }
        } finally {
            foreach (var dir in new[] { sub, super, subUpstream, superUpstream })
                try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// A gitlink whose working-tree directory is a SYMLINK must not cause git to be invoked outside
    /// the source tree. ContainedPath is lexical — Path.GetFullPath does not resolve links — so the
    /// escaped path passes containment while `git -C` follows the link and enumerates a directory the
    /// snapshot has no right to read. The per-file guards downstream cannot retroactively undo a
    /// directory-level traversal that already happened.
    /// </summary>
    [Test]
    public async Task BorrowedSnapshot_RefusesSubmodulePathThatIsASymlink() {
        var (_, super) = MakeUpstreamWithSideRef("refs/pull/93/head", out _);
        using var tmp = new TempDir();
        var outside = tmp.CreateDir("outside");
        var root    = tmp.PathTo("root");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "must-not-be-snapshotted");

        // Forge a gitlink entry whose path is a symlink to a directory outside the source.
        Directory.CreateSymbolicLink(Path.Combine(super, "vendored"), outside);
        Git(super, "update-index", "--add", "--cacheinfo",
            "160000," + new string('a', 40) + ",vendored");

        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.CreateBorrowedSnapshotAsync(super, "review", CancellationToken.None));
        await Assert.That(ex!.Message).StartsWith("borrowed_snapshot_symlink_unsupported");
    }

    [Test]
    public async Task BorrowedSnapshot_IsIndependent_CopiesDirtyContext_AndRefreshesPristinely() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/88/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
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
                await manager.SyncFromSourceAsync(clone, clone, snapshot.Path, [], CancellationToken.None);

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
        }
    }

    [Test]
    public async Task BorrowedSnapshot_RefreshPreservesRunningExecutionDirectory() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/90/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
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
                psi.RedirectStandardOutput = true;
                if (OperatingSystem.IsWindows()) {
                    psi.ArgumentList.Add("/d");
                    psi.ArgumentList.Add("/c");
                    psi.ArgumentList.Add("echo ready& ping -n 30 127.0.0.1 >nul");
                } else {
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("echo ready; sleep 30");
                }
                holder = Process.Start(psi);
                // Wait for the child to say it is up rather than for a fixed 200ms: the assertions
                // below only mean anything once a live process is holding snapshot.Path as its cwd,
                // and on a loaded runner process start can take longer than any guess.
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await Assert.That((await holder!.StandardOutput.ReadLineAsync(ready.Token))?.Trim()).IsEqualTo("ready");

                File.WriteAllText(Path.Combine(sourceCwd, "round.txt"), "two");
                File.WriteAllText(Path.Combine(snapshot.Path, "reviewer-created.txt"), "remove");
                await manager.SyncFromSourceAsync(
                    clone, sourceCwd, snapshot.SnapshotRoot!, [], CancellationToken.None);

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
        }
    }

    [Test]
    public async Task BorrowedSnapshot_RejectsSymlinkWithoutFollowingIt() {
        Skip.When(OperatingSystem.IsWindows(), "Symlink semantics in this certification are POSIX-only.");
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/89/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
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
        }
    }

    [Test]
    public async Task Snapshot_copy_rejects_linked_destination_parent_before_touching_target() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        using var tmp = new TempDir();
        var root = tmp.CreateDir("root");
        var external = tmp.CreateDir("external");
        var linkedParent = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(linkedParent, external);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorktreeManager.EnsureParentDirectories(
                root, Path.Combine(linkedParent, "child", "file.txt")));

        await Assert.That(ex.Message)
            .StartsWith("borrowed_snapshot_destination_symlink_unsupported");
        await Assert.That(Directory.Exists(Path.Combine(external, "child"))).IsFalse();
    }

    [Test]
    public async Task Snapshot_copy_rejects_linked_destination_leaf_without_truncating_target() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        using var tmp = new TempDir();
        var root = tmp.CreateDir("root");
        var external = Path.Combine(tmp.CreateDir("external"), "secret.txt");
        File.WriteAllText(external, "keep-me");
        var linkedLeaf = Path.Combine(root, "file.txt");
        File.CreateSymbolicLink(linkedLeaf, external);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorktreeManager.EnsureDestinationLeafNoFollow(linkedLeaf));

        await Assert.That(ex.Message)
            .StartsWith("borrowed_snapshot_destination_symlink_unsupported");
        await Assert.That(File.ReadAllText(external)).IsEqualTo("keep-me");
    }

    [Test]
    public async Task Snapshot_build_rejects_branch_linked_parent_before_copying_dirty_child() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/91/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
        var external = tmp.CreateDir("external");
        try {
            var route = Path.Combine(clone, "linked-parent");
            Directory.CreateSymbolicLink(route, external);
            Git(clone, "add", "linked-parent");
            Git(clone, "commit", "-q", "-m", "branch parent link");
            Directory.Delete(route);
            Directory.CreateDirectory(route);
            File.WriteAllText(Path.Combine(route, "dirty.txt"), "dirty working bytes");

            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await manager.CreateBorrowedSnapshotAsync(clone, "review", CancellationToken.None));

            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_destination_symlink_unsupported");
            await Assert.That(File.Exists(Path.Combine(external, "dirty.txt"))).IsFalse();
        } finally {
            try { Directory.Delete(upstream, true); } catch { }
            try { Directory.Delete(clone, true); } catch { }
        }
    }

    [Test]
    public async Task Snapshot_build_rejects_branch_linked_leaf_without_truncating_target() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/92/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
        var externalDir = tmp.CreateDir("external");
        var external = Path.Combine(externalDir, "sentinel.txt");
        try {
            File.WriteAllText(external, "keep-me");
            var route = Path.Combine(clone, "linked-leaf");
            File.CreateSymbolicLink(route, external);
            Git(clone, "add", "linked-leaf");
            Git(clone, "commit", "-q", "-m", "branch leaf link");
            File.Delete(route);
            File.WriteAllText(route, "dirty working bytes");

            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await manager.CreateBorrowedSnapshotAsync(clone, "review", CancellationToken.None));

            await Assert.That(ex!.Message)
                .StartsWith("borrowed_snapshot_destination_symlink_unsupported");
            await Assert.That(File.ReadAllText(external)).IsEqualTo("keep-me");
        } finally {
            try { Directory.Delete(upstream, true); } catch { }
            try { Directory.Delete(clone, true); } catch { }
        }
    }

    [Test]
    [Arguments("dir\\file.txt")]
    [Arguments("line\nbreak.txt")]
    [Arguments("café.txt")]
    public async Task Snapshot_paths_that_require_identity_rewriting_are_rejected(string path) {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorktreeManager.NormalizeRelativePath(path));
        await Assert.That(ex.Message).StartsWith("borrowed_snapshot_invalid_path");
    }

    [Test]
    public async Task Snapshot_path_validation_preserves_valid_replacement_character_exactly() {
        const string path = "replacement-�.txt";
        await Assert.That(WorktreeManager.NormalizeRelativePath(path)).IsEqualTo(path);
    }

    [Test]
    public async Task Snapshot_build_rejects_linked_source_parent_instead_of_copying_external_bytes() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/93/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
        var external = tmp.CreateDir("external");
        try {
            var trackedDir = Path.Combine(clone, "tracked-dir");
            Directory.CreateDirectory(trackedDir);
            File.WriteAllText(Path.Combine(trackedDir, "child.txt"), "public");
            Git(clone, "add", "tracked-dir/child.txt");
            Git(clone, "commit", "-q", "-m", "tracked child");
            Directory.Delete(trackedDir, true);
            File.WriteAllText(Path.Combine(external, "child.txt"), "private external bytes");
            Directory.CreateSymbolicLink(trackedDir, external);

            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await manager.CreateBorrowedSnapshotAsync(clone, "review", CancellationToken.None));

            await Assert.That(ex!.Message).StartsWith("borrowed_snapshot_symlink_unsupported");
        } finally {
            try { Directory.Delete(upstream, true); } catch { }
            try { Directory.Delete(clone, true); } catch { }
        }
    }

    [Test]
    public async Task Snapshot_removes_stale_head_alias_using_destination_case_policy() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/94/head", out _);
        using var tmp = new TempDir();
        var root = tmp.Path;
        WorktreeInfo? snapshot = null;
        try {
            Skip.When(!WorktreeManager.ProbeCaseSensitiveFileSystem(root),
                "The stale spelling is distinct only on a case-sensitive filesystem.");

            File.WriteAllText(Path.Combine(clone, "Alias.txt"), "old committed spelling");
            Git(clone, "add", "Alias.txt");
            Git(clone, "commit", "-q", "-m", "add alias");
            Git(clone, "mv", "Alias.txt", "alias.txt");
            File.WriteAllText(Path.Combine(clone, "alias.txt"), "new staged spelling");

            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);
            snapshot = await manager.CreateBorrowedSnapshotAsync(
                clone, "review", CancellationToken.None);

            await Assert.That(File.Exists(Path.Combine(snapshot.Path, "Alias.txt"))).IsFalse();
            await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "alias.txt")))
                .IsEqualTo("new staged spelling");
        } finally {
            if (snapshot is not null) await WorktreeManager.RemoveAsync(snapshot);
            try { Directory.Delete(upstream, true); } catch { }
            try { Directory.Delete(clone, true); } catch { }
        }
    }

    [Test]
    public async Task CleanupOrphaned_PreservesActiveBorrowedSnapshotContainerAndRemovesOnlyOrphans() {
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
        var activeRoot = Path.Combine(root, "borrowed-snapshots", "active");
        var activeCwd = Path.Combine(activeRoot, "src");
        var activeSidecar = WorktreeManager.ReviewContextRootFor(activeRoot);
        var orphan = Path.Combine(root, "borrowed-snapshots", "orphan");
        var orphanSidecar = WorktreeManager.ReviewContextRootFor(orphan);
        var legacy = Path.Combine(root, "legacy-orphan");
        Directory.CreateDirectory(activeCwd);
        Directory.CreateDirectory(activeSidecar);
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(orphanSidecar);
        Directory.CreateDirectory(legacy);
        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);

        await manager.CleanupOrphanedAsync([activeCwd]);

        await Assert.That(Directory.Exists(activeRoot)).IsTrue();
        await Assert.That(Directory.Exists(activeSidecar)).IsTrue();
        await Assert.That(Directory.Exists(orphan)).IsFalse();
        await Assert.That(Directory.Exists(orphanSidecar)).IsFalse();
        await Assert.That(Directory.Exists(legacy)).IsFalse();
    }

    [Test]
    public async Task CleanupOrphaned_unlinks_borrowed_snapshot_root_without_following_target() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        using var tmp = new TempDir();
        var root = tmp.CreateDir("root");
        var external = tmp.CreateDir("external");
        var borrowedSnapshots = Path.Combine(root, "borrowed-snapshots");
        var externalChild = Path.Combine(external, "must-survive");
        try {
            Directory.CreateDirectory(externalChild);
            File.WriteAllText(Path.Combine(externalChild, "sentinel.txt"), "keep-me");
            Directory.CreateSymbolicLink(borrowedSnapshots, external);
            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);

            await manager.CleanupOrphanedAsync();

            await Assert.That(Path.Exists(borrowedSnapshots)).IsFalse();
            await Assert.That(Directory.Exists(externalChild)).IsTrue();
            await Assert.That(File.ReadAllText(Path.Combine(externalChild, "sentinel.txt")))
                .IsEqualTo("keep-me");
        } finally {
            try { if (Path.Exists(borrowedSnapshots)) File.Delete(borrowedSnapshots); } catch { }
        }
    }

    /// <summary>A live launch's vendor state directory survives the sweep, and a dead one does not.
    ///
    /// <para>It holds the reviewer's whole <c>HOME</c> for the launch and is never itself an active
    /// worktree path, so the sweep's plain active-path rule would delete it mid-review — a failure that
    /// would present as an unreproducible vendor crash rather than as anything pointing here.</para></summary>
    [Test]
    public async Task CleanupOrphaned_KeepsALiveVendorStateDirectoryAndRemovesADeadOne() {
        using var tmp = new TempDir();
        var root       = tmp.PathTo("root");
        var snapshots  = Path.Combine(root, "borrowed-snapshots");
        var activeRoot = Path.Combine(snapshots, "active");
        var activeCwd  = Path.Combine(activeRoot, "src");
        var liveState  = WorktreeManager.VendorStateRootFor(activeRoot);
        var deadState  = WorktreeManager.VendorStateRootFor(Path.Combine(snapshots, "gone"));
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
    }

    /// <summary>An active SNAPSHOT whose own name ends in the state-directory suffix is not deleted.
    ///
    /// <para>Snapshot directories are named from the agent id, so this shape is reachable. Treating the
    /// suffix as a classification — deriving an "owner" and testing only that — compares a live snapshot
    /// against a path that does not exist and deletes the reviewer's worktree. The suffix is a hint;
    /// the directory's own activeness is always checked first.</para></summary>
    [Test]
    public async Task CleanupOrphaned_KeepsAnActiveSnapshotWhoseNameEndsWithTheStateSuffix() {
        using var tmp = new TempDir();
        var root       = tmp.PathTo("root");
        var activeRoot = Path.Combine(root, "borrowed-snapshots", "borrowed-agent" + WorktreeManager.VendorStateSuffix);
        var activeCwd  = Path.Combine(activeRoot, "src");
        Directory.CreateDirectory(activeCwd);
        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance);

        await manager.CleanupOrphanedAsync([activeCwd]);

        await Assert.That(Directory.Exists(activeCwd)).IsTrue()
            .Because("an active snapshot must survive regardless of what its name happens to end with");
    }
}
