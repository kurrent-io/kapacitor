using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class BootSeedTests {
    static LaunchConsentStore Store(string dir) => new(dir, NullLogger.Instance);
    static string PolicyPath(string dir) => Path.Combine(dir, "consent.json");

    [Test]
    public async Task Absent_file_seeds_prompt_with_seed_source() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Seeded);
        var json = await File.ReadAllTextAsync(PolicyPath(dir));
        await Assert.That(json).Contains("\"default\": \"prompt\"");
        await Assert.That(json).Contains("\"default_source\": \"seed\"");
    }

    [Test]
    public async Task Operator_allow_survives_reseed() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        await File.WriteAllTextAsync(PolicyPath(dir),
            """{"default":"allow","prompt_timeout_seconds":45,"rules":[],"default_source":"operator"}""");
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Respected);
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(dir))).Contains("\"allow\"");
    }

    [Test]
    public async Task Unstamped_factory_looking_allow_is_rewritten_to_prompt() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        await File.WriteAllTextAsync(PolicyPath(dir),
            """{"default":"allow","prompt_timeout_seconds":45,"rules":[]}""");
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Rewritten);
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(dir))).Contains("\"prompt\"");
    }

    [Test]
    public async Task Allow_with_rules_is_respected() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        await File.WriteAllTextAsync(PolicyPath(dir),
            """{"default":"allow","prompt_timeout_seconds":45,"rules":[{"action":"deny","requester":"x"}]}""");
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Respected);
    }

    [Test]
    public async Task Malformed_file_is_quarantined_and_seeded() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        await File.WriteAllTextAsync(PolicyPath(dir), "{not json");
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Quarantined);
        await Assert.That(Directory.GetFiles(dir, "consent.json.quarantined-*")).IsNotEmpty();
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(dir))).Contains("\"prompt\"");
    }

    [Test]
    public async Task Unrecognized_default_value_is_a_silent_allow_arm_and_gets_quarantined() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        await File.WriteAllTextAsync(PolicyPath(dir), """{"default":"totally-bogus","rules":[]}""");
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Quarantined);
    }

    [Test]
    [Arguments("")] [Arguments("allow")] [Arguments("deny")] [Arguments("Prompt")] [Arguments("bogus")]
    public async Task Non_literal_prompt_directives_refuse(string directive) {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        var r = Store(dir).BootSeed(directive);
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.RefusedInvalidDirective);
        await Assert.That(r.RefusalToken).IsEqualTo("consent_seed_invalid");
        await Assert.That(File.Exists(PolicyPath(dir))).IsFalse();
    }

    [Test]
    public async Task Operator_put_stamps_operator_source() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        var store = Store(dir);
        store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45, []), out _);
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(dir))).Contains("\"default_source\": \"operator\"");
        // and a later reseed respects it:
        var r = store.BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Respected);
    }
}
