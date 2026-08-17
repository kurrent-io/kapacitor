using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class BootSeedTests {
    static LaunchConsentStore Store(string dir) => new(dir, NullLogger.Instance);
    static string PolicyPath(string dir) => Path.Combine(dir, "consent.json");

    [Test]
    public async Task Absent_file_seeds_prompt_with_seed_source() {
        using var tmp = new TempDir();
        var r = Store(tmp.Path).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Seeded);
        var json = await File.ReadAllTextAsync(PolicyPath(tmp.Path));
        await Assert.That(json).Contains("\"default\": \"prompt\"");
        await Assert.That(json).Contains("\"default_source\": \"seed\"");
    }

    [Test]
    public async Task Operator_allow_survives_reseed() {
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(PolicyPath(tmp.Path),
            """{"default":"allow","prompt_timeout_seconds":45,"rules":[],"default_source":"operator"}""");
        var r = Store(tmp.Path).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Respected);
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(tmp.Path))).Contains("\"allow\"");
    }

    [Test]
    public async Task Unstamped_factory_looking_allow_is_rewritten_to_prompt() {
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(PolicyPath(tmp.Path),
            """{"default":"allow","prompt_timeout_seconds":45,"rules":[]}""");
        var r = Store(tmp.Path).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Rewritten);
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(tmp.Path))).Contains("\"prompt\"");
    }

    [Test]
    public async Task Allow_with_rules_is_respected() {
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(PolicyPath(tmp.Path),
            """{"default":"allow","prompt_timeout_seconds":45,"rules":[{"action":"deny","requester":"x"}]}""");
        var r = Store(tmp.Path).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Respected);
    }

    [Test]
    public async Task Malformed_file_is_quarantined_and_seeded() {
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(PolicyPath(tmp.Path), "{not json");
        var r = Store(tmp.Path).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Quarantined);
        await Assert.That(Directory.GetFiles(tmp.Path, "consent.json.quarantined-*")).IsNotEmpty();
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(tmp.Path))).Contains("\"prompt\"");
    }

    [Test]
    public async Task Unrecognized_default_value_is_a_silent_allow_arm_and_gets_quarantined() {
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(PolicyPath(tmp.Path), """{"default":"totally-bogus","rules":[]}""");
        var r = Store(tmp.Path).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Quarantined);
    }

    // ── malformed-rule matrix: a recognized `default` with a structurally bad rules array must
    // never count as Respected — ToPolicy silently drops the bad rule, so "allow" + one bogus rule
    // would otherwise land as an effective zero-rule allow indistinguishable from the pre-consent
    // factory default. Quarantine + reseed instead, same as any other malformed file. ──

    [Test]
    public async Task Allow_with_bogus_rule_action_is_a_silent_allow_arm_and_gets_quarantined() {
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(PolicyPath(tmp.Path), """{"default":"allow","rules":[{"action":"bogus"}]}""");
        var r = Store(tmp.Path).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Quarantined);
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(tmp.Path))).Contains("\"prompt\"");
    }

    [Test]
    public async Task Allow_with_a_null_rule_element_is_quarantined_not_thrown() {
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(PolicyPath(tmp.Path), """{"default":"allow","rules":[null]}""");
        var r = Store(tmp.Path).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Quarantined);
    }

    [Test]
    public async Task Allow_with_invalid_rule_kind_is_quarantined() {
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(PolicyPath(tmp.Path),
            """{"default":"allow","rules":[{"action":"allow","kind":"not-a-real-kind"}]}""");
        var r = Store(tmp.Path).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Quarantined);
    }

    [Test]
    public async Task Deny_default_with_a_null_rule_element_is_quarantined_not_respected() {
        // Without the rules check, "deny" would classify Respected untouched — but the real store's
        // later Load() would NRE inside ToPolicy on the null element and fall back to
        // LaunchConsentPolicy.UpgradeSafe (Allow), inverting the operator's deny default. Catching
        // it here means the file is reseeded to a clean prompt default before that ever happens.
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(PolicyPath(tmp.Path), """{"default":"deny","rules":[null]}""");
        var r = Store(tmp.Path).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Quarantined);
    }

    [Test]
    public async Task Allow_with_valid_rules_including_a_recognized_kind_is_still_respected() {
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(PolicyPath(tmp.Path),
            """{"default":"allow","rules":[{"action":"allow","kind":"review"}]}""");
        var r = Store(tmp.Path).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Respected);
    }

    [Test]
    [Arguments("")] [Arguments("allow")] [Arguments("deny")] [Arguments("Prompt")] [Arguments("bogus")]
    public async Task Non_literal_prompt_directives_refuse(string directive) {
        using var tmp = new TempDir();
        var r = Store(tmp.Path).BootSeed(directive);
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.RefusedInvalidDirective);
        await Assert.That(r.RefusalToken).IsEqualTo("consent_seed_invalid");
        await Assert.That(File.Exists(PolicyPath(tmp.Path))).IsFalse();
    }

    [Test]
    public async Task Operator_put_stamps_operator_source() {
        using var tmp = new TempDir();
        var store = Store(tmp.Path);
        store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45, []), out _);
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(tmp.Path))).Contains("\"default_source\": \"operator\"");
        // and a later reseed respects it:
        var r = store.BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Respected);
    }

    // Pins the seed->gate handoff without a live DaemonRunner.RunAsync/server
    // round trip. A "prompt" boot seed on an absent file classifies Seeded and persists a
    // Prompt-default policy (asserted above by other tests); this test documents that the SAME
    // store, read back into a gate with no prompter (no UI attached), then denies an immediate
    // launch fail-closed. The gate's own prompt_no_ui behavior is pinned by LaunchConsentGateTests
    // (Prompt_without_subscriber_denies_no_ui) — this test is the seed->gate wiring, not a
    // duplicate of that contract.
    [Test]
    public async Task Seeded_policy_denies_an_immediate_launch_with_no_ui() {
        using var tmp = new TempDir();
        var store = Store(tmp.Path);
        store.BootSeed("prompt");
        await Assert.That(store.Current.Default).IsEqualTo(LaunchConsentDefault.Prompt);
        // Gate behavior for Prompt + no prompter is pinned by the existing launch-consent gate
        // tests (prompt_no_ui → deny); this assertion documents the seed→gate linkage.
    }
}
