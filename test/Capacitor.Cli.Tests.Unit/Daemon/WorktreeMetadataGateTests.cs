using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Pins the per-repository serialisation around the git commands that touch <c>.git/worktrees</c>.
/// <para>What is observed: concurrent launches in one repo can kill a <c>git worktree add</c> with
/// <c>failed to read .git/worktrees/&lt;other&gt;/commondir: Success</c>. The precise interleaving is
/// inferred rather than reproduced — the window is too narrow to trigger on demand (it survived 12
/// consecutive runs of the concurrent end-to-end test), which is exactly why the guarantee is asserted
/// directly here instead of through a flaky repro: same repo excludes, different repos still run in
/// parallel, equivalent spellings share a gate, and the permit survives a throwing mutation.</para>
/// </summary>
public class WorktreeMetadataGateTests {
    static string TempRepo() =>
        Path.Combine(Path.GetTempPath(), "kcap-gate-" + Guid.NewGuid().ToString("N")[..12]);

    // RunContinuationsAsynchronously: without it a SetResult can run the waiter's continuation inline
    // on this thread, which would make the ordering below prove nothing.
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task Second_mutation_on_the_same_repo_waits_for_the_first() {
        var repo = TempRepo();

        var firstEntered  = Signal();
        var releaseFirst  = Signal();
        var secondEntered = Signal();

        var first = WorktreeManager.WithWorktreeMetadataGate(repo, async () => {
            firstEntered.SetResult();
            await releaseFirst.Task;
        });

        await firstEntered.Task; // the gate is now held

        var second = WorktreeManager.WithWorktreeMetadataGate(repo, () => {
            secondEntered.SetResult();

            return Task.CompletedTask;
        });

        // Must still be waiting. Deleting the gate makes this the failing assertion.
        var raced = await Task.WhenAny(secondEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
        await Assert.That(ReferenceEquals(raced, secondEntered.Task)).IsFalse();

        releaseFirst.SetResult();
        await first;
        await second;

        await Assert.That(secondEntered.Task.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task Mutations_on_different_repos_are_not_serialised() {
        var repoA = TempRepo();
        var repoB = TempRepo();

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

        // Both must be inside at once — a single global gate (rather than per-repo) would leave the
        // second one queued and this wait would time out.
        var both      = Task.WhenAll(aEntered.Task, bEntered.Task);
        var completed = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(10)));
        await Assert.That(ReferenceEquals(completed, both)).IsTrue();

        release.SetResult();
        await Task.WhenAll(a, b);
    }

    [Test]
    public async Task Equivalent_spellings_of_one_repo_share_a_gate() {
        var repo = TempRepo();
        Directory.CreateDirectory(repo);

        try {
            // Same directory, spelled with a trailing separator and via a "." segment: the gate keys
            // on the normalised full path, so these must exclude each other.
            var alias = Path.Combine(repo, ".") + Path.DirectorySeparatorChar;

            var firstEntered  = Signal();
            var releaseFirst  = Signal();
            var secondEntered = Signal();

            var first = WorktreeManager.WithWorktreeMetadataGate(repo, async () => {
                firstEntered.SetResult();
                await releaseFirst.Task;
            });

            await firstEntered.Task;

            var second = WorktreeManager.WithWorktreeMetadataGate(alias, () => {
                secondEntered.SetResult();

                return Task.CompletedTask;
            });

            var raced = await Task.WhenAny(secondEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
            await Assert.That(ReferenceEquals(raced, secondEntered.Task)).IsFalse();

            releaseFirst.SetResult();
            await first;
            await second;
        } finally {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    static void Git(string cwd, params string[] args) {
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args) {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {proc.StandardError.ReadToEnd()}");
    }

    [Test]
    public async Task A_linked_worktree_shares_the_gate_with_its_main_checkout() {
        // The metadata being protected is the SHARED .git/worktrees, so two checkouts of ONE repository
        // must exclude each other even though their paths differ. Keying on the checkout path (rather
        // than rev-parse --git-common-dir) would let these two run concurrently — the exact unguarded
        // add this change exists to prevent.
        var root   = TempRepo();
        var main   = Path.Combine(root, "main");
        var linked = Path.Combine(root, "linked");
        Directory.CreateDirectory(main);

        try {
            Git(main, "init", "-q", ".");
            Git(main, "config", "user.email", "t@t");
            Git(main, "config", "user.name", "t");
            File.WriteAllText(Path.Combine(main, "a.txt"), "a");
            Git(main, "add", "-A");
            Git(main, "commit", "-q", "-m", "init");
            Git(main, "worktree", "add", "-q", linked, "-b", "side");

            var firstEntered  = Signal();
            var releaseFirst  = Signal();
            var secondEntered = Signal();

            var first = WorktreeManager.WithWorktreeMetadataGate(main, async () => {
                firstEntered.SetResult();
                await releaseFirst.Task;
            });

            await firstEntered.Task;

            var second = WorktreeManager.WithWorktreeMetadataGate(linked, () => {
                secondEntered.SetResult();

                return Task.CompletedTask;
            });

            var raced = await Task.WhenAny(secondEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
            await Assert.That(ReferenceEquals(raced, secondEntered.Task)).IsFalse();

            releaseFirst.SetResult();
            await first;
            await second;
        } finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Gate_is_released_when_the_mutation_throws() {
        var repo = TempRepo();

        await Assert.That(async () => await WorktreeManager.WithWorktreeMetadataGate(
            repo, () => throw new InvalidOperationException("git failed"))).Throws<InvalidOperationException>();

        // A leaked permit would hang the next launch on this repo forever, so prove the gate reopens.
        var second    = WorktreeManager.WithWorktreeMetadataGate(repo, () => Task.CompletedTask);
        var completed = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10)));
        await Assert.That(ReferenceEquals(completed, second)).IsTrue();
    }
}
