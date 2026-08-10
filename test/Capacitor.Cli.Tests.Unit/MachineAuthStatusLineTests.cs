using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// `kcap status` warns when machine-credential environment variables are diverting this CLI's auth
/// off the profile token store. Because <see cref="MachineAuth.Intended"/> triggers on EITHER
/// variable but <see cref="MachineAuth.TryRead"/> needs BOTH, the message distinguishes the two:
/// both set means the CLI records as the machine; one set means the credential is incomplete and
/// nothing records (the diversion still bypasses the token store, so `kcap login` is not the fix).
/// It names exactly the variable(s) present and says nothing when neither is set.
/// </summary>
public class MachineAuthStatusLineTests {
    [Test]
    public async Task Both_variables_present_says_it_records_as_the_machine() {
        var line = MachineAuth.DescribeDiversion(idSet: true, secretSet: true);
        await Assert.That(line).IsNotNull();
        await Assert.That(line!).Contains("KCAP_CLIENT_ID and KCAP_CLIENT_SECRET set");
        await Assert.That(line!).Contains("records as the machine, not as your login");
    }

    [Test]
    [Arguments(true,  false, "KCAP_CLIENT_ID is set but KCAP_CLIENT_SECRET is not")]
    [Arguments(false, true,  "KCAP_CLIENT_SECRET is set but KCAP_CLIENT_ID is not")]
    public async Task One_variable_present_reports_an_incomplete_credential(bool id, bool secret, string expectedFragment) {
        var line = MachineAuth.DescribeDiversion(id, secret);
        await Assert.That(line).IsNotNull();
        await Assert.That(line!).Contains("incomplete");
        await Assert.That(line!).Contains(expectedFragment);
        await Assert.That(line!).Contains("diverted");
        await Assert.That(line!).DoesNotContain("records as the machine")
            .Because("an incomplete credential does not record — TryRead refuses it");
    }

    [Test]
    public async Task Silent_when_neither_variable_is_present() {
        await Assert.That(MachineAuth.DescribeDiversion(false, false)).IsNull();
    }
}
