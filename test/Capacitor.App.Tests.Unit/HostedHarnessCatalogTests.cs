using Capacitor.App.Services;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Tests.Unit;

public class HostedHarnessCatalogTests {
    [Test]
    public async Task Advertised_vendors_are_available_and_others_are_not() {
        var options = HostedHarnessCatalog.Build(["claude", "cursor"]);

        await Assert.That(options.Single(o => o.Vendor == "claude").Available).IsTrue();
        await Assert.That(options.Single(o => o.Vendor == "cursor").Available).IsTrue();
        await Assert.That(options.Single(o => o.Vendor == "pi").Available).IsFalse();
    }

    [Test]
    public async Task Unknown_vendor_set_leaves_everything_available() {
        var options = HostedHarnessCatalog.Build(null);
        await Assert.That(options.All(o => o.Available)).IsTrue();
    }

    [Test]
    public async Task Transport_family_matches_how_the_daemon_hosts_each_vendor() {
        var options = HostedHarnessCatalog.Build(null).ToDictionary(o => o.Vendor);

        await Assert.That(options["claude"].TransportFamily).IsEqualTo("pty");
        await Assert.That(options["codex"].TransportFamily).IsEqualTo("pty");
        await Assert.That(options["cursor"].TransportFamily).IsEqualTo("acp");
        await Assert.That(options["opencode"].TransportFamily).IsEqualTo("acp");
        await Assert.That(options["antigravity"].TransportFamily).IsEqualTo("rpc");
        await Assert.That(options["pi"].TransportFamily).IsEqualTo("rpc");
    }

    /// The vendor list comes from Core, the transport map does not: a tenth vendor added there
    /// would fall through to "rpc" and be labelled "chat" in the picker with nobody the wiser.
    /// This turns that into a red suite on the PR that adds it. The runtime fallback stays — a
    /// vendor only the DAEMON knows about must still be listed, not dropped.
    [Test]
    public async Task Every_core_vendor_has_an_explicit_transport_family() {
        var mapped = new HashSet<string>(HostedHarnessCatalog.MappedVendors, StringComparer.OrdinalIgnoreCase);
        var unmapped = HarnessCatalog.All.Select(k => k.VendorId).Where(v => !mapped.Contains(v)).ToList();

        await Assert.That(unmapped).IsEmpty();
    }

    [Test]
    public async Task An_unknown_advertised_vendor_is_listed_rather_than_dropped() {
        var options = HostedHarnessCatalog.Build(["claude", "brandnew"]);

        var added = options.Single(o => o.Vendor == "brandnew");
        await Assert.That(added.Available).IsTrue();
        await Assert.That(added.Label).IsEqualTo("brandnew");
    }

    [Test]
    public async Task Description_names_the_transport_and_the_surface() {
        var pty = HostedHarnessCatalog.Build(null).Single(o => o.Vendor == "claude");
        var acp = HostedHarnessCatalog.Build(null).Single(o => o.Vendor == "gemini");

        await Assert.That(HostedHarnessCatalog.DescriptionFor(pty)).IsEqualTo("PTY · terminal + chat");
        await Assert.That(HostedHarnessCatalog.DescriptionFor(acp)).IsEqualTo("ACP · chat");
    }
}
