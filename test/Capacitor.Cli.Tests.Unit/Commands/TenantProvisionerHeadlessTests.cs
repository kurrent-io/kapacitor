using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Spectre.Console;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// AI-2052 — found by running the real CLI, not by these tests. Arming the provisioner for headless
/// sessions (which the device grant made reachable) sent a piped stdin straight into a Spectre
/// SelectionPrompt, and Spectre throws rather than returning, so `kcap login --discover` crashed with
/// NotSupportedException after a SUCCESSFUL sign-in. The façade tests never saw it because they
/// substitute ITenantProvisioner, so the prompt these cover never runs there.
/// </summary>
public class TenantProvisionerHeadlessTests {
    static WorkOSTokenSource Tokens() =>
        new("access-token", refreshToken: null, (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

    [Test]
    public async Task Declines_instead_of_throwing_when_there_is_no_terminal_to_prompt_on() {
        // Asserted, not assumed: this is the condition under test, and a test host that happened to
        // own a TTY would otherwise pass it vacuously.
        await Assert.That(AnsiConsole.Profile.Capabilities.Interactive).IsFalse();

        var provisioner = new SpectreTenantProvisioner(
            new TenantProvisioningClient(new HttpClient()), "https://signup.example");

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
