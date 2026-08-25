using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Picking between workspaces is a Spectre prompt, and Spectre throws from inside one rather than
/// returning. Discovery reaches it whenever an account has more than one, which a scripted setup can.
/// </summary>
[NotInParallel]
public class TenantPickerHeadlessTests {
    static DiscoveredTenant[] Two() => [
        new() { Provider = AuthProvider.WorkOS, OrganizationId = "org_1", Slug = "acme",   DisplayName = "Acme",   Origin = "https://acme.kcap.ai" },
        new() { Provider = AuthProvider.WorkOS, OrganizationId = "org_2", Slug = "globex", DisplayName = "Globex", Origin = "https://globex.kcap.ai" }
    ];

    [Test]
    public async Task Returns_nothing_instead_of_throwing_when_there_is_no_terminal_to_prompt_on() {
        using var capture = ConsoleOutput.StartErrorCapture();
        var picker = new SpectreTenantPicker(isInteractive: () => false);

        var picked = await picker.PickAsync(Two(), CancellationToken.None);

        await Assert.That(picked).IsNull();
    }

    [Test]
    public async Task Names_every_workspace_so_the_run_can_be_repeated_against_one() {
        using var capture = ConsoleOutput.StartErrorCapture();
        var picker = new SpectreTenantPicker(isInteractive: () => false);

        await picker.PickAsync(Two(), CancellationToken.None);

        var written = capture.GetCapturedError();

        await Assert.That(written).Contains("https://acme.kcap.ai");
        await Assert.That(written).Contains("https://globex.kcap.ai");
        await Assert.That(written).Contains("--server-url");
    }
}
