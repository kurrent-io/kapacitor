using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class HarnessInventoryTests {
    static HarnessOfferLedger LedgerWith(params (string Vendor, HarnessOfferEntry Entry)[] rows) =>
        new() { Vendors = rows.ToDictionary(r => r.Vendor, r => r.Entry, StringComparer.Ordinal) };

    [Test]
    public async Task Covers_all_nine_vendors_with_detected_and_wired() {
        var inv = HarnessInventory.Evaluate(
            TestHarnesses.All([HarnessId.Cursor, HarnessId.Antigravity], wired: [HarnessId.Cursor]),
            new HarnessOfferLedger(),
            "machine-1");

        await Assert.That(inv.MachineId).IsEqualTo("machine-1");
        await Assert.That(inv.Vendors.Count).IsEqualTo(9);

        await Assert.That(inv.Vendors["antigravity"]).IsEqualTo(new HarnessInventoryEntry(Detected: true, Wired: false));
        await Assert.That(inv.Vendors["cursor"]).IsEqualTo(new HarnessInventoryEntry(Detected: true, Wired: true));
        await Assert.That(inv.Vendors["gemini"]).IsEqualTo(new HarnessInventoryEntry(Detected: false, Wired: false));
    }

    [Test]
    public async Task Declined_lists_only_dismissed_vendors() {
        var inv = HarnessInventory.Evaluate(
            TestHarnesses.All([HarnessId.Antigravity]),
            LedgerWith(("antigravity", new HarnessOfferEntry { Declined = true }),
                       ("gemini", new HarnessOfferEntry { LastOffered = DateTimeOffset.UtcNow })),
            "m");

        await Assert.That(inv.Declined).IsEquivalentTo(new[] { "antigravity" });
    }

    [Test]
    public async Task No_dismissals_yields_empty_declined() {
        var inv = HarnessInventory.Evaluate(TestHarnesses.All(), new HarnessOfferLedger(), "m");
        await Assert.That(inv.Declined).IsEmpty();
    }
}
