using System.Diagnostics;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Mutates the process-wide <see cref="SkillsAutoSync.ProcessStarterForTesting"/> seam,
/// so the class runs alone.</summary>
[NotInParallel]
public class SkillsAutoSyncTests {
    [Test]
    public async Task Throttle_skips_within_the_interval_and_runs_past_or_outside_it() {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        static SkillsManifest M(DateTimeOffset t) => new() { SyncedAt = t };

        await Assert.That(SkillsCommand.AutoThrottled(M(now.AddHours(-1)), now)).IsTrue();
        await Assert.That(SkillsCommand.AutoThrottled(M(now.AddHours(-7)), now)).IsFalse();
        await Assert.That(SkillsCommand.AutoThrottled(null, now)).IsFalse();                    // no ledger ⇒ sync
        await Assert.That(SkillsCommand.AutoThrottled(new() { SyncedAt = null }, now)).IsFalse();
        // A future stamp (clock correction, tampered file) is stale, never an unbounded suppression.
        await Assert.That(SkillsCommand.AutoThrottled(M(now.AddHours(2)), now)).IsFalse();
    }

    [Test]
    public async Task Spawn_is_detached_quiet_and_never_throws() {
        ProcessStartInfo? seen = null;
        SkillsAutoSync.ProcessStarterForTesting = psi => { seen = psi; return null; };
        try {
            SkillsAutoSync.SpawnDetached("/some/repo");
            await Assert.That(seen).IsNotNull();
            await Assert.That(seen!.ArgumentList.ToArray()).IsEquivalentTo(new[] { "skills", "sync", "--auto" });
            await Assert.That(seen.WorkingDirectory).IsEqualTo("/some/repo");
            await Assert.That(seen.RedirectStandardOutput).IsTrue();   // the hook's stdout is a data channel

            SkillsAutoSync.ProcessStarterForTesting = _ => throw new InvalidOperationException("boom");
            SkillsAutoSync.SpawnDetached("/some/repo");                // swallowed — never breaks a hook
        } finally {
            SkillsAutoSync.ProcessStarterForTesting = null;
        }
    }
}
