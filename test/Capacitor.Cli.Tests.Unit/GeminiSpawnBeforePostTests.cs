using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Task 6: Gemini's session-start is routed through <see cref="AgentHookPoster.PostOrSpoolAsync"/>
/// (was the POST-only <c>PostAsync</c>, which returned WITHOUT spawning on anything but <c>Posted</c>).
/// <see cref="GeminiHookCommand.SpawnGateForTest"/> exposes the same spawn decision as
/// <see cref="AgentHookPoster.ShouldSpawnAfter"/> so a spooled outcome (auth lapse / outage) still
/// spawns the watcher — capture must not depend on lifecycle-POST delivery.
/// </summary>
public class GeminiSpawnBeforePostTests {
    [Test]
    public async Task spooled_outcome_still_spawns_watcher() {
        await Assert.That(GeminiHookCommand.SpawnGateForTest(HookPostOutcome.Posted, "http://localhost:5108")).IsTrue();
        await Assert.That(GeminiHookCommand.SpawnGateForTest(HookPostOutcome.Spooled, "http://localhost:5108")).IsTrue();
        await Assert.That(GeminiHookCommand.SpawnGateForTest(HookPostOutcome.Failed, "http://localhost:5108")).IsFalse();
    }

    [Test]
    public async Task Spawn_gate_refuses_an_unusable_url() {
        // Pins that production passes a real URL through this gate: with a default value here the
        // conjunct silently never fired for this vendor.
        await Assert.That(GeminiHookCommand.SpawnGateForTest(HookPostOutcome.Spooled, "ftp://host")).IsFalse();
    }
}
