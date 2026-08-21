using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class CliResolverTests {
    [Test]
    public async Task ParseVersion_single_line_parses_the_version() {
        await Assert.That(CliResolver.ParseVersion("kcap 1.2.3\n")).IsEqualTo("1.2.3");
    }

    [Test]
    public async Task ParseVersion_keeps_build_metadata() {
        await Assert.That(CliResolver.ParseVersion("kcap 1.2.3+abc")).IsEqualTo("1.2.3+abc");
    }

    [Test]
    public async Task ParseVersion_multiline_is_null() {
        await Assert.That(CliResolver.ParseVersion("kcap 1.2.3\nextra noise\n")).IsNull();
    }

    [Test]
    public async Task ParseVersion_unknown_is_null() {
        await Assert.That(CliResolver.ParseVersion("kcap unknown")).IsNull();
    }

    [Test]
    public async Task ParseVersion_missing_prefix_is_null() {
        await Assert.That(CliResolver.ParseVersion("1.2.3")).IsNull();
    }

    [Test]
    public async Task ParseVersion_empty_is_null() {
        await Assert.That(CliResolver.ParseVersion("")).IsNull();
    }

    [Test]
    public async Task ResolvePath_prefers_the_override_when_it_exists() {
        var path = CliResolver.ResolvePath(_ => "/opt/kcap/kcap", p => p == "/opt/kcap/kcap");

        await Assert.That(path).IsEqualTo("/opt/kcap/kcap");
    }

    [Test]
    public async Task ResolvePath_broken_override_is_null_not_a_silent_fallback() {
        var path = CliResolver.ResolvePath(_ => "/opt/kcap/kcap", _ => false);

        await Assert.That(path).IsNull();
    }

    [Test]
    public async Task ResolvePath_no_override_falls_back_to_kcap_on_path() {
        var path = CliResolver.ResolvePath(_ => null, _ => false);

        await Assert.That(path).IsEqualTo("kcap");
    }

    [Test]
    public async Task ResolvePath_empty_override_is_treated_as_unset() {
        var path = CliResolver.ResolvePath(_ => "", _ => false);

        await Assert.That(path).IsEqualTo("kcap");
    }
}
