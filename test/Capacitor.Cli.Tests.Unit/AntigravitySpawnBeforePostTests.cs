using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Task 6: Antigravity is routed through <see cref="AgentHookPoster.PostOrSpoolAsync(string,string,string,string,Capacitor.Cli.Core.HookSpool,string,string)"/>
/// (its own bespoke poster previously gated the watcher on <c>exit == 0</c>, which never spawned
/// on a lapse/outage). <see cref="AntigravityHookCommand.SpawnGateForTest"/> exposes the same
/// spawn decision as <see cref="AgentHookPoster.ShouldSpawnAfter"/> so a spooled outcome still
/// spawns the watcher — capture must not depend on lifecycle delivery.
/// </summary>
public class AntigravitySpawnBeforePostTests {
    [Test]
    public async Task spooled_outcome_still_spawns_watcher() {
        await Assert.That(AntigravityHookCommand.SpawnGateForTest(HookPostOutcome.Spooled, "http://localhost:5108")).IsTrue();
        await Assert.That(AntigravityHookCommand.SpawnGateForTest(HookPostOutcome.Failed, "http://localhost:5108")).IsFalse();
    }

    [Test]
    public async Task Spawn_gate_refuses_an_unusable_url() {
        // Pins that production passes a real URL through this gate: with a default value here the
        // conjunct silently never fired for this vendor.
        await Assert.That(AntigravityHookCommand.SpawnGateForTest(HookPostOutcome.Spooled, "ftp://host")).IsFalse();
    }
}
