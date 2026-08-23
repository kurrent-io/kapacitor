using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class HarnessCatalogTests {
    [Test]
    public async Task Advertised_vendors_are_available_and_others_are_not() {
        var options = HarnessCatalog.Build(["claude", "cursor"]);

        await Assert.That(options.Single(o => o.Vendor == "claude").Available).IsTrue();
        await Assert.That(options.Single(o => o.Vendor == "cursor").Available).IsTrue();
        await Assert.That(options.Single(o => o.Vendor == "pi").Available).IsFalse();
    }

    [Test]
    public async Task Unknown_vendor_set_leaves_everything_available() {
        var options = HarnessCatalog.Build(null);
        await Assert.That(options.All(o => o.Available)).IsTrue();
    }

    [Test]
    public async Task Transport_family_matches_how_the_daemon_hosts_each_vendor() {
        var options = HarnessCatalog.Build(null).ToDictionary(o => o.Vendor);

        await Assert.That(options["claude"].TransportFamily).IsEqualTo("pty");
        await Assert.That(options["codex"].TransportFamily).IsEqualTo("pty");
        await Assert.That(options["cursor"].TransportFamily).IsEqualTo("acp");
        await Assert.That(options["opencode"].TransportFamily).IsEqualTo("acp");
        await Assert.That(options["antigravity"].TransportFamily).IsEqualTo("rpc");
        await Assert.That(options["pi"].TransportFamily).IsEqualTo("rpc");
    }

    [Test]
    public async Task An_unknown_advertised_vendor_is_listed_rather_than_dropped() {
        var options = HarnessCatalog.Build(["claude", "brandnew"]);

        var added = options.Single(o => o.Vendor == "brandnew");
        await Assert.That(added.Available).IsTrue();
        await Assert.That(added.Label).IsEqualTo("brandnew");
    }

    [Test]
    public async Task Description_names_the_transport_and_the_surface() {
        var pty = HarnessCatalog.Build(null).Single(o => o.Vendor == "claude");
        var acp = HarnessCatalog.Build(null).Single(o => o.Vendor == "gemini");

        await Assert.That(HarnessCatalog.DescriptionFor(pty)).IsEqualTo("PTY · terminal + chat");
        await Assert.That(HarnessCatalog.DescriptionFor(acp)).IsEqualTo("ACP · chat");
    }
}
