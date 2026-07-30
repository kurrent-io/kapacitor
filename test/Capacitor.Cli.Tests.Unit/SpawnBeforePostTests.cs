using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Task 4: the spawn-before-post decision for the JSON-payload vendor dispatchers
/// (Kiro, OpenCode, Pi, Copilot). Capture must start on <c>Posted</c> OR <c>Spooled</c> — never
/// gated behind lifecycle-POST delivery. Only a permanent <c>Failed</c> withholds the watcher.
/// </summary>
public class SpawnBeforePostTests {
    [Test]
    public async Task spawn_after_posted_or_spooled_only() {
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Posted, "http://localhost:5108")).IsTrue();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Spooled, "http://localhost:5108")).IsTrue();
        // AuthLapsed (legacy PostAsync path) spools NOTHING, so spawning there would tail a session
        // whose SessionStarted was permanently dropped — must NOT spawn.
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.AuthLapsed, "http://localhost:5108")).IsFalse();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Failed, "http://localhost:5108")).IsFalse();
    }
}
