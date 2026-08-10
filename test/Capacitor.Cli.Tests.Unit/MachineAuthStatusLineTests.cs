using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// `kcap status` warns when machine-credential environment variables are diverting ALL of this
/// CLI's auth off the profile token store. Because <see cref="MachineAuth.Intended"/> triggers on
/// EITHER variable, the message must name whichever one(s) are actually present — a fixed "ID is
/// set" line would be false in the secret-only case — and say nothing when neither is set.
/// </summary>
public class MachineAuthStatusLineTests {
    [Test]
    [Arguments(true,  false, "KCAP_CLIENT_ID is set")]
    [Arguments(false, true,  "KCAP_CLIENT_SECRET is set")]
    [Arguments(true,  true,  "KCAP_CLIENT_ID and KCAP_CLIENT_SECRET are set")]
    public async Task Names_exactly_the_variables_present(bool id, bool secret, string expectedFragment) {
        var line = MachineAuth.DescribeDiversion(id, secret);
        await Assert.That(line).IsNotNull();
        await Assert.That(line!).Contains(expectedFragment);
        await Assert.That(line!).Contains("records as the machine, not as your login");
    }

    [Test]
    public async Task Silent_when_neither_variable_is_present() {
        await Assert.That(MachineAuth.DescribeDiversion(false, false)).IsNull();
    }
}
