using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class DaemonConsentCommandTests {
    [Test]
    public async Task BuildRule_maps_flags_to_rule_fields() {
        var rule = DaemonConsentCommand.TryBuildRule("deny",
            ["--requester", "user_x", "--kind", "review-flow", "--vendor", "codex"], out var error);
        await Assert.That(error).IsNull();
        await Assert.That(rule!.Action).IsEqualTo("deny");
        await Assert.That(rule.Requester).IsEqualTo("user_x");
        await Assert.That(rule.Kind).IsEqualTo("review-flow");
        await Assert.That(rule.Repo).IsNull();
        await Assert.That(rule.Vendor).IsEqualTo("codex");
    }

    [Test]
    public async Task BuildRule_rejects_flagless_and_unknown_flags_and_bad_kind() {
        await Assert.That(DaemonConsentCommand.TryBuildRule("allow", [], out var e1)).IsNull();
        await Assert.That(e1).Contains("at least one");
        await Assert.That(DaemonConsentCommand.TryBuildRule("allow", ["--nope", "x"], out var e2)).IsNull();
        await Assert.That(e2).Contains("--nope");
        await Assert.That(DaemonConsentCommand.TryBuildRule("allow", ["--kind", "flows"], out var e3)).IsNull();
        await Assert.That(e3).Contains("kind");
    }
}
