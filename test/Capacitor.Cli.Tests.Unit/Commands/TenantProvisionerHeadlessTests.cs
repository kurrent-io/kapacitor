using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A non-interactive session reaches the create-a-workspace fork, where every way out is a Spectre
/// prompt and Spectre throws rather than returning. The façade tests substitute ITenantProvisioner, so
/// the prompt these cover never runs there.
/// </summary>
public class TenantProvisionerHeadlessTests {
    static WorkOSTokenSource Tokens() =>
        new("access-token", refreshToken: null, (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

    /// <summary>Interactivity is injected, not read: the ambient value belongs to whatever host the
    /// suite is running under, so reading it would pass in CI and fail in a developer's terminal.</summary>
    [Test]
    public async Task Declines_instead_of_throwing_when_there_is_no_terminal_to_prompt_on() {
        var provisioner = new SpectreTenantProvisioner(
            new TenantProvisioningClient(new HttpClient()), "https://signup.example",
            isInteractive: () => false);

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Declined);
    }

    /// <summary>Names the two routes that actually work, and no third: an org admin cannot fix this,
    /// so "ask your admin" would be a dead end dressed as advice.</summary>
    [Test]
    public async Task The_message_offers_signup_and_an_existing_workspace() {
        var message = OAuthLoginFlow.WorkspaceCreationNeedsATerminalMessage();

        await Assert.That(message).Contains("/signup");
        await Assert.That(message).Contains("--server-url");
        await Assert.That(message).DoesNotContain("admin");
    }
}
