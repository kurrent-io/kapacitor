using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Tests.Unit;

/// Pins the Core harness catalog against the CLI's hand-maintained vendor-flag list, so adding a
/// tenth installable harness fails HERE rather than silently missing every nudge/status surface.
public class HarnessCatalogConformanceTests {
    [Test]
    public async Task Catalog_flags_match_known_vendor_flags_exactly() {
        var catalogFlags = HarnessCatalog.All.Select(h => "--" + h.VendorId).OrderBy(x => x).ToArray();
        var knownFlags   = VendorSelection.KnownVendorFlags.OrderBy(x => x).ToArray();
        await Assert.That(catalogFlags).IsEquivalentTo(knownFlags);
    }

    [Test]
    public async Task Every_known_vendor_flag_resolves_to_a_catalog_entry() {
        foreach (var flag in VendorSelection.KnownVendorFlags) {
            var id = flag.TrimStart('-');
            await Assert.That(HarnessCatalog.ById(id)).IsNotNull();
        }
    }
}
