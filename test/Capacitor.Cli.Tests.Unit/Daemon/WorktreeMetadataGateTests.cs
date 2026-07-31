using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Pins the per-repository serialisation around the git commands that mutate <c>.git/worktrees</c>.
/// <para>The race this prevents — two concurrent <c>git worktree add</c> calls on one repo, where one
/// reads a sibling's half-written metadata and dies with
/// <c>failed to read .git/worktrees/&lt;other&gt;/commondir</c> — has too narrow a window to reproduce
/// on demand (it survived 12 consecutive runs of the concurrent end-to-end test). So the guarantee is
/// asserted directly here: same repo excludes, different repos still run in parallel, and equivalent
/// spellings of one path share a gate.</para>
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
