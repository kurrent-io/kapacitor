using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// <see cref="DaemonRunner.RetainAdvertisedVersions"/>: a re-probe that fails must not replace a
/// version already advertised. The server reads a null version as the vendor being gone, so
/// publishing one over a transient probe miss would withdraw the reviewer.
/// </summary>
public class DaemonRunnerCapabilityRefreshTests {
    static UnattendedVendorCapability Cap(string vendor, string? version, bool borrowed = false) =>
        new(vendor, version, $"{vendor}-unattended-v1", borrowed);

    [Test]
    public async Task A_failed_reprobe_keeps_the_previously_advertised_version() {
        var merged = DaemonRunner.RetainAdvertisedVersions([Cap("claude", "2.1.259")], [Cap("claude", null)]);

        await Assert.That(merged.Single().CliVersion).IsEqualTo("2.1.259");
    }

    [Test]
    public async Task A_successful_reprobe_replaces_the_advertised_version() {
        var merged = DaemonRunner.RetainAdvertisedVersions([Cap("claude", "2.1.259")], [Cap("claude", "2.1.263")]);

        await Assert.That(merged.Single().CliVersion).IsEqualTo("2.1.263");
    }

    [Test]
    public async Task A_vendor_never_advertised_with_a_version_stays_unversioned() {
        var merged = DaemonRunner.RetainAdvertisedVersions([Cap("claude", null)], [Cap("claude", null)]);

        await Assert.That(merged.Single().CliVersion).IsNull();
    }

    [Test]
    public async Task A_vendor_absent_from_the_reprobe_is_dropped() {
        var merged = DaemonRunner.RetainAdvertisedVersions(
            [Cap("claude", "2.1.259"), Cap("codex", "0.153.0")], [Cap("claude", "2.1.263")]);

        await Assert.That(merged.Select(c => c.Vendor)).IsEquivalentTo(["claude"]);
    }

    [Test]
    public async Task With_nothing_advertised_yet_the_reprobe_is_returned_unchanged() {
        var fresh  = new[] { Cap("claude", null), Cap("codex", "0.153.0") };
        var merged = DaemonRunner.RetainAdvertisedVersions(null, fresh);

        await Assert.That(merged).IsEquivalentTo(fresh);
    }

    [Test]
    public async Task Every_field_but_the_version_comes_from_the_reprobe() {
        var merged = DaemonRunner.RetainAdvertisedVersions(
            [Cap("codex", "0.153.0", borrowed: false)], [Cap("codex", null, borrowed: true)]);

        await Assert.That(merged.Single()).IsEqualTo(Cap("codex", "0.153.0", borrowed: true));
    }
}
