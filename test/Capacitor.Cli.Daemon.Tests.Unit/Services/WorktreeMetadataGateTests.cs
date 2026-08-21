using System.Collections.Concurrent;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Pins the per-repository serialisation around the git commands that touch <c>.git/worktrees</c>.
/// <para>What is observed: concurrent launches in one repo can kill a <c>git worktree add</c> with
/// <c>failed to read .git/worktrees/&lt;other&gt;/commondir: Success</c>. Why that happens is a
/// hypothesis, not something these tests establish — and the window is too narrow to trigger on demand
/// (it survived 12 consecutive runs of the concurrent end-to-end test). That is exactly why the
/// guarantee is asserted directly here rather than through a flaky repro.</para>
/// <para>The exclusion tests assert ORDERING — the second holder entered only after the first released
/// — rather than an elapsed-time bound on the operation itself. That is strictly stronger, but be clear
/// about the residual assumption: the second acquisition must reach the semaphore within the settle
/// window below, or the expected order would also appear WITHOUT exclusion. There is no seam to observe
/// "reached the gate", so the window is sized generously against work that takes microseconds. The real
/// evidence that these assertions bite is mutation testing: deleting the gate fails all three of them
/// (an earlier version of this file used IsEquivalentTo, which ignores ordering, and passed with the
/// gate deleted — hence the positional assertions).</para>
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class WorktreeMetadataGateTests {
    // RunContinuationsAsynchronously: without it a SetResult can run the waiter's continuation inline
    // on this thread, which would make the ordering below prove nothing.
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Takes and releases the gate once so the path's key is resolved and cached. Keeps the
    /// git spawn out of the timed section below.</summary>
    static Task WarmGateKey(string path) =>
        WorktreeManager.WithWorktreeMetadataGate(path, () => Task.CompletedTask);

    /// <summary>Holds the gate for <paramref name="firstPath"/>, starts a second acquisition for
    /// <paramref name="secondPath"/>, gives an ungated implementation ample room to slip in, then
    /// releases and asserts the second entered strictly after the release.</summary>
    static async Task AssertSecondWaitsForFirst(string firstPath, string secondPath) {
        await WarmGateKey(firstPath);
        await WarmGateKey(secondPath);

        var log          = new ConcurrentQueue<string>();
        var firstEntered = Signal();
        var releaseFirst = Signal();

        var first = WorktreeManager.WithWorktreeMetadataGate(firstPath, async () => {
            log.Enqueue("first-enter");
            firstEntered.SetResult();
            await releaseFirst.Task;
        });

        await firstEntered.Task;

        var second = WorktreeManager.WithWorktreeMetadataGate(secondPath, () => {
            log.Enqueue("second-enter");

            return Task.CompletedTask;
        });

        // Without a gate the second acquisition takes microseconds, so this is ample room for it to
        // record "second-enter" before the release marker and fail the ordering below.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        log.Enqueue("release");
        releaseFirst.SetResult();

        await first.WaitAsync(TimeSpan.FromSeconds(30));
        await second.WaitAsync(TimeSpan.FromSeconds(30));

        // Positional, NOT IsEquivalentTo — that ignores ordering by default, which makes every
        // permutation pass and the whole assertion vacuous (mutation testing caught exactly that).
        var order = log.ToArray();
        await Assert.That(order.Length).IsEqualTo(3);
        await Assert.That(order[0]).IsEqualTo("first-enter");
        await Assert.That(order[1]).IsEqualTo("release");
        await Assert.That(order[2]).IsEqualTo("second-enter");
    }

    [Test]
    public async Task Second_mutation_on_the_same_repo_waits_for_the_first() {
        using var repo = new TempDir();
        await AssertSecondWaitsForFirst(repo.Path, repo.Path);
    }

    [Test]
    public async Task Equivalent_spellings_of_one_repo_share_a_gate() {
        using var repo = new TempDir();

        // Same directory, spelled with a "." segment and a trailing separator: the key is the
        // normalised full path, so these must exclude each other.
        await AssertSecondWaitsForFirst(repo.Path, repo.PathTo(".") + Path.DirectorySeparatorChar);
    }

    [Test]
    public async Task A_symlinked_spelling_of_one_repo_shares_its_gate() {
        // Callers do supply aliased spellings — a launch cwd arrives over local IPC as whatever the
        // client sent, and on macOS /tmp is itself a symlink to /private/tmp. A lexical-only key would
        // hand these two spellings different gates and leave one repository unguarded.
        if (OperatingSystem.IsWindows()) return; // creating a symlink needs elevation on Windows

        using var root = new TempDir();
        var real   = root.PathTo("real");
        var alias  = root.PathTo("alias");
        Directory.CreateDirectory(real);

        Directory.CreateSymbolicLink(alias, real);
        await AssertSecondWaitsForFirst(real, alias);
    }

    [Test]
    public async Task A_linked_worktree_shares_the_gate_with_its_main_checkout() {
        // The metadata being protected is the SHARED .git/worktrees, so two checkouts of ONE repository
        // must exclude each other even though their paths differ. Keying on the checkout path (rather
        // than rev-parse --git-common-dir) lets these run concurrently — the exact unguarded add this
        // change exists to prevent.
        using var root = new TempDir();
        var main   = root.PathTo("main");
        var linked = root.PathTo("linked");
        Directory.CreateDirectory(main);

        Git(main, "init", "-q", ".");
        Git(main, "config", "user.email", "t@t");
        Git(main, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(main, "a.txt"), "a");
        Git(main, "add", "-A");
        Git(main, "commit", "-q", "-m", "init");
        Git(main, "worktree", "add", "-q", linked, "-b", "side");

        await AssertSecondWaitsForFirst(main, linked);
    }

    [Test]
    public async Task Mutations_on_different_repos_are_not_serialised() {
        using var tmp = new TempDir();
        var repoA = tmp.PathTo("repoA");
        var repoB = tmp.PathTo("repoB");

        var aEntered = Signal();
        var bEntered = Signal();
        var release  = Signal();

        var a = WorktreeManager.WithWorktreeMetadataGate(repoA, async () => {
            aEntered.SetResult();
            await release.Task;
        });
        var b = WorktreeManager.WithWorktreeMetadataGate(repoB, async () => {
            bEntered.SetResult();
            await release.Task;
        });

        // Both must be inside at once — one global gate instead of a per-repo one would leave the
        // second queued and this wait would time out.
        await Task.WhenAll(aEntered.Task, bEntered.Task).WaitAsync(TimeSpan.FromSeconds(30));

        release.SetResult();
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task Gate_is_released_when_the_mutation_throws() {
        using var repo = new TempDir();

        await Assert.That(async () => await WorktreeManager.WithWorktreeMetadataGate(
            repo.Path, () => throw new InvalidOperationException("git failed"))).Throws<InvalidOperationException>();

        // A leaked permit would hang the next launch on this repo forever, so prove the gate reopens.
        await WorktreeManager.WithWorktreeMetadataGate(repo.Path, () => Task.CompletedTask)
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    static void Git(string cwd, params string[] args) {
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args) {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {proc.StandardError.ReadToEnd()}");
    }
}
