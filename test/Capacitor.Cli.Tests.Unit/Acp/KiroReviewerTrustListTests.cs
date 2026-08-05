using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The trust list is scoped, so it must enumerate everything the launch injects. A FIXED list naming
/// only the result tool is the defect this type exists to prevent: an allowlisted server's tools
/// would raise permission frames and the Fail policy would kill the round.
/// </summary>
public class KiroReviewerTrustListTests {
    static AcpMcpServerSpec Spec(string name) => new(name, "kcap", ["mcp", "x"], []);

    static LaunchIdentity Aliasing() => LaunchIdentity.ForLaunch(aliasResultChannel: true);

    [Test]
    public async Task CarriesTheNativeReadAndThinkTools() {
        var identity = Aliasing();
        var entries  = KiroReviewerTrustList
            .Build([Spec(identity.ResultChannelWireName)], identity).Split(',');

        await Assert.That(entries).Contains("fs_read");
        await Assert.That(entries).Contains("thinking");
    }

    /// <summary>
    /// Trusting shell would let a write execute with no permission frame at all, so the read-only
    /// posture would be fiction. This is the assertion that keeps the scoped set actually scoped.
    /// </summary>
    [Test]
    public async Task NeverTrustsWriteOrShell() {
        var identity = Aliasing();
        var value    = KiroReviewerTrustList.Build([Spec(identity.ResultChannelWireName)], identity);

        await Assert.That(value).DoesNotContain("fs_write");
        await Assert.That(value).DoesNotContain("execute_bash");
    }

    /// <summary>
    /// Every unattended-safe tool of the result channel, read from the catalog — NOT just
    /// submit_review_result. send_flow_message is unattended-safe too, and a reviewer that cannot
    /// call it silently loses the out-of-band message lane.
    /// </summary>
    [Test]
    public async Task NamespacesEveryUnattendedSafeResultChannelTool() {
        var identity = Aliasing();
        var wire     = identity.ResultChannelWireName;
        var entries  = KiroReviewerTrustList.Build([Spec(wire)], identity).Split(',');

        foreach (var tool in KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools)
            await Assert.That(entries).Contains($"@{wire}/{tool}");
    }

    /// <summary>
    /// The case a FIXED trust list fails. Every injected allowlist server's tools must be trusted, or
    /// the first call raises a frame and Fail ends the round.
    /// </summary>
    [Test]
    public async Task IncludesEveryToolOfEveryInjectedAllowlistServer() {
        var identity   = Aliasing();
        var reviewWire = identity.AllowlistWireName("kcap-review");
        var entries    = KiroReviewerTrustList
            .Build([Spec(identity.ResultChannelWireName), Spec(reviewWire)], identity).Split(',');

        foreach (var tool in KcapMcpRegistry.ReviewFlowUnattendedSafeTools["kcap-review"])
            await Assert.That(entries).Contains($"@{reviewWire}/{tool}");
    }

    /// <summary>The wire names must come from the SAME identity the specs were built from. A name
    /// this identity did not produce is not resolvable, and the launch fails rather than shipping a
    /// reviewer that cannot call what it was given.</summary>
    [Test]
    public async Task RejectsAServerNameFromADifferentIdentity() {
        var identity = Aliasing();
        var stranger = Aliasing().AllowlistWireName("kcap-review");

        await Assert.That(() => KiroReviewerTrustList
                .Build([Spec(identity.ResultChannelWireName), Spec(stranger)], identity))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RejectsAnInjectedServerWithNoSafeToolTableEntry() {
        var identity = Aliasing();

        await Assert.That(() => KiroReviewerTrustList
                .Build([Spec(identity.ResultChannelWireName), Spec("totally-unknown")], identity))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task BuildArgvCarriesTheFlagAndTheValue() {
        var identity = Aliasing();
        var argv     = KiroReviewerTrustList.BuildArgv([Spec(identity.ResultChannelWireName)], identity);

        await Assert.That(argv.Length).IsEqualTo(2);
        await Assert.That(argv[0]).IsEqualTo("--trust-tools");
        await Assert.That(argv[1]).Contains("fs_read");
    }
}
