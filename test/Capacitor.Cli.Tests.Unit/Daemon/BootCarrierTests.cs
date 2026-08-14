using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Boot-local carrier lifecycle for <c>KCAP_CONSENT_SEED_DEFAULT</c>,
/// <c>KCAP_EXPECT_SERVER_URL</c> and <c>KCAP_BOOT_ATTEMPT</c> — captured off ambient env into
/// <see cref="DaemonConfig"/> and immediately removed so no descendant process (PTY-spawned agent,
/// ACP child, self-respawned successor) can observe them by inheritance. Re-injection into a
/// self-respawned successor is deliberately partial: <see cref="DaemonRunner.BootCarriers.Attempt"/>
/// is per-launch-action and a self-respawn is not the app's action, so only Seed/Expect cross over.
/// </summary>
public class BootCarrierTests {
    [Test]
    public async Task Capture_reads_all_three_and_removes_them_from_ambient() {
        var env = new Dictionary<string, string?> {
            [DaemonRunner.BootCarriers.Seed] = "prompt",
            [DaemonRunner.BootCarriers.Expect] = "https://s.example",
            [DaemonRunner.BootCarriers.Attempt] = "att-1",
            ["OTHER"] = "kept",
        };
        var config = new DaemonConfig();
        DaemonRunner.CaptureBootCarriers(config, k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(config.ConsentSeedDirective).IsEqualTo("prompt");
        await Assert.That(config.ExpectedServerUrl).IsEqualTo("https://s.example");
        await Assert.That(config.BootAttemptId).IsEqualTo("att-1");
        await Assert.That(env.ContainsKey(DaemonRunner.BootCarriers.Seed)).IsFalse();
        await Assert.That(env.ContainsKey(DaemonRunner.BootCarriers.Expect)).IsFalse();
        await Assert.That(env.ContainsKey(DaemonRunner.BootCarriers.Attempt)).IsFalse();
        await Assert.That(env["OTHER"]).IsEqualTo("kept");
    }

    /// <summary>Exact-value contract: a set-but-EMPTY seed directive must read back as <c>""</c>,
    /// not null — collapsing it to null would make an empty directive indistinguishable from one
    /// never set at all, defeating BootSeed's own <c>""</c> → RefusedInvalidDirective classification.
    /// Uses the injectable dictionary seam (not real process env) so this is not sensitive to a
    /// platform's own set-but-empty-vs-unset quirks.</summary>
    [Test]
    public async Task Capture_preserves_a_set_but_empty_seed_directive_as_empty_not_null() {
        var env = new Dictionary<string, string?> {
            [DaemonRunner.BootCarriers.Seed] = "",
        };
        var config = new DaemonConfig();
        DaemonRunner.CaptureBootCarriers(config, k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(config.ConsentSeedDirective).IsEqualTo("");
        await Assert.That(config.ConsentSeedDirective).IsNotNull();
    }

    [Test]
    public async Task Respawn_successor_env_reinjects_seed_and_expectation_but_not_attempt() {
        var config = new DaemonConfig {
            ConsentSeedDirective = "prompt", ExpectedServerUrl = "https://s.example", BootAttemptId = "att-1",
        };
        var env = DetachedRespawnStrategy.SuccessorEnvOverlay(config);

        await Assert.That(env[DaemonRunner.BootCarriers.Seed]).IsEqualTo("prompt");
        await Assert.That(env[DaemonRunner.BootCarriers.Expect]).IsEqualTo("https://s.example");
        // an attempt id is per-ACTION; a self-respawn is not the app's action:
        await Assert.That(env.ContainsKey(DaemonRunner.BootCarriers.Attempt)).IsFalse();
    }
}
