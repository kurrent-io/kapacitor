using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class VendorSelectionTests {
    [Test]
    public async Task parses_opencode_flag() {
        var r = VendorSelection.Parse(new[] { "import", "--opencode" });
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors).Contains("opencode");
    }

    [Test]
    public async Task rejects_opencode_prefixed_unknown_flag() {
        var r = VendorSelection.Parse(new[] { "import", "--opencode-foo" });
        await Assert.That(r.HasError).IsTrue();
    }

    [Test]
    public async Task parses_dsh_flag() {
        var r = VendorSelection.Parse(new[] { "import", "--dsh" });
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors).Contains("dsh");
    }

    [Test]
    public async Task rejects_dsh_prefixed_unknown_flag() {
        var r = VendorSelection.Parse(new[] { "import", "--dsh-foo" });
        await Assert.That(r.HasError).IsTrue();
    }
}
