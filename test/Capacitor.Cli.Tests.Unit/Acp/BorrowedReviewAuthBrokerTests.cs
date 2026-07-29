using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Where a contained borrowed reviewer's credential comes from.
///
/// <para>The point of brokering is that the sandbox no longer grants <c>~/Library/Keychains</c> — a
/// recursive, credential-bearing tree that was reachable with no ACP interaction frame, so the
/// <c>Fail</c> policy never fired. Verified live: with a brokered token and no keychain grant
/// <c>session/new</c> succeeds, and without one it answers <c>Authentication required</c>, which is
/// what establishes the token is genuinely carrying the authentication rather than something else
/// having cached it.</para>
/// </summary>
public class BorrowedReviewAuthBrokerTests {
    static Func<string, string?> Env(params (string Name, string? Value)[] entries) =>
        name => entries.FirstOrDefault(e => e.Name == name).Value;

    [Test]
    public async Task No_configured_variable_resolves_to_null() {
        await Assert.That(BorrowedReviewAuthBroker.TryResolve(Env())).IsNull();
    }

    [Test]
    [Arguments("COPILOT_GITHUB_TOKEN")]
    [Arguments("GH_TOKEN")]
    [Arguments("GITHUB_TOKEN")]
    public async Task Any_single_configured_variable_resolves(string name) {
        await Assert.That(BorrowedReviewAuthBroker.TryResolve(Env((name, "tok-1")))).IsEqualTo("tok-1");
    }

    /// <summary>Precedence matches the vendor's own, so brokering cannot select a different credential
    /// than an unsandboxed run would have used — a reviewer authenticating as a different identity
    /// than the user expects is its own kind of surprise.</summary>
    [Test]
    public async Task Resolution_follows_the_vendors_own_precedence_order() {
        var all = BorrowedReviewAuthBroker.TryResolve(Env(
            ("COPILOT_GITHUB_TOKEN", "vendor-specific"),
            ("GH_TOKEN",             "gh"),
            ("GITHUB_TOKEN",         "github")));
        var withoutVendorSpecific = BorrowedReviewAuthBroker.TryResolve(Env(
            ("GH_TOKEN",     "gh"),
            ("GITHUB_TOKEN", "github")));

        await Assert.That(all).IsEqualTo("vendor-specific");
        await Assert.That(withoutVendorSpecific).IsEqualTo("gh");
        await Assert.That(BorrowedReviewAuthBroker.SourceVariables.ToArray()).IsEquivalentTo(
            ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"]);
    }

    /// <summary>A variable set to whitespace is not a credential. Treating it as one would advertise
    /// borrowed review on a daemon that cannot authenticate, which is the failure mode gating support
    /// on the broker exists to avoid.</summary>
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("\t")]
    public async Task A_blank_variable_is_not_a_credential(string blank) {
        await Assert.That(BorrowedReviewAuthBroker.TryResolve(Env(("GH_TOKEN", blank)))).IsNull();
    }

    [Test]
    public async Task A_blank_variable_does_not_shadow_a_real_one_later_in_the_order() {
        var resolved = BorrowedReviewAuthBroker.TryResolve(Env(
            ("COPILOT_GITHUB_TOKEN", ""),
            ("GH_TOKEN",             "real")));

        await Assert.That(resolved).IsEqualTo("real");
    }
}
