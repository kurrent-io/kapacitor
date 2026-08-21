using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class SlugValidatorTests {
    [Test]
    [Arguments("acme", true, null)]
    [Arguments("a", true, null)]
    [Arguments("ab-cd", true, null)]
    [Arguments("-acme", false, "invalid")]
    [Arguments("acme-", false, "invalid")]
    [Arguments("ac--me", false, "invalid")]
    [Arguments("Acme", false, "invalid")]        // uppercase (validate expects canonical)
    [Arguments("kcap", false, "blocked")]
    [Arguments("api", false, "blocked")]
    public async Task Validate_classifies_slug(string slug, bool ok, string? reason) {
        var check = SlugValidator.Validate(slug);
        await Assert.That(check.Ok).IsEqualTo(ok);
        await Assert.That(check.Reason).IsEqualTo(reason);
    }

    [Test]
    [Arguments("Acme Inc",          "acme-inc")]
    [Arguments("  Hello, World!  ", "hello-world")]
    [Arguments("Café Déjà",         "cafe-deja")]
    [Arguments("multi   space",     "multi-space")]
    [Arguments("--dashes--",        "dashes")]
    public async Task Derive_produces_a_canonical_slug(string orgName, string expected) {
        await Assert.That(SlugValidator.Derive(orgName)).IsEqualTo(expected);
    }

    [Test]
    public async Task Derive_truncates_to_40_chars() {
        var derived = SlugValidator.Derive(new string('a', 60));
        await Assert.That(derived.Length).IsEqualTo(40);
    }

    [Test]
    public async Task Url_defaults_to_capacitor_kurrent_io() {
        await Assert.That(ProvisioningEndpoint.DefaultUrl).IsEqualTo("https://capacitor.kurrent.io");
    }
}
