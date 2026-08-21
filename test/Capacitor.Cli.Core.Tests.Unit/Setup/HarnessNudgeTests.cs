using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class HarnessNudgeTests {
    static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    static HarnessOfferLedger LedgerWith(params (string Vendor, HarnessOfferEntry Entry)[] rows) =>
        new() { Vendors = rows.ToDictionary(r => r.Vendor, r => r.Entry, StringComparer.Ordinal) };

    static IReadOnlyList<string> NudgeableIds(
            AgentDetectionResult detected, Func<string, bool> isWired, HarnessOfferLedger ledger) =>
        HarnessNudge.Nudgeable(detected, isWired, ledger, Now).Select(h => h.VendorId).ToList();

    [Test]
    public async Task Detected_unwired_never_offered_is_nudgeable() {
        var ids = NudgeableIds(HarnessCatalogTests.DetectionWithOnly("antigravity"), _ => false, new HarnessOfferLedger());
        await Assert.That(ids).Contains("antigravity");
    }

    [Test]
    public async Task Wired_vendor_is_not_nudgeable() {
        var ids = NudgeableIds(HarnessCatalogTests.DetectionWithOnly("antigravity"), _ => true, new HarnessOfferLedger());
        await Assert.That(ids).IsEmpty();
    }

    [Test]
    public async Task Undetected_vendor_is_not_nudgeable() {
        var ids = NudgeableIds(HarnessCatalogTests.DetectionWithOnly(), _ => false, new HarnessOfferLedger());
        await Assert.That(ids).IsEmpty();
    }

    [Test]
    public async Task Declined_vendor_is_not_nudgeable() {
        var ledger = LedgerWith(("antigravity", new HarnessOfferEntry { Declined = true }));
        var ids = NudgeableIds(HarnessCatalogTests.DetectionWithOnly("antigravity"), _ => false, ledger);
        await Assert.That(ids).IsEmpty();
    }

    [Test]
    public async Task Offered_within_floor_is_not_nudgeable() {
        var ledger = LedgerWith(("antigravity", new HarnessOfferEntry { LastOffered = Now.AddDays(-3) }));
        var ids = NudgeableIds(HarnessCatalogTests.DetectionWithOnly("antigravity"), _ => false, ledger);
        await Assert.That(ids).IsEmpty();
    }

    [Test]
    public async Task Offered_past_floor_is_nudgeable_again() {
        var ledger = LedgerWith(("antigravity", new HarnessOfferEntry { LastOffered = Now.AddDays(-8) }));
        var ids = NudgeableIds(HarnessCatalogTests.DetectionWithOnly("antigravity"), _ => false, ledger);
        await Assert.That(ids).Contains("antigravity");
    }

    [Test]
    public async Task Multiple_detected_unwired_all_nudgeable_in_catalog_order() {
        var ids = NudgeableIds(HarnessCatalogTests.DetectionWithOnly("gemini", "antigravity"), _ => false, new HarnessOfferLedger());
        // catalog order is Gemini before Antigravity
        await Assert.That(ids).IsEquivalentTo(new[] { "gemini", "antigravity" });
    }

    [Test]
    public async Task Wired_predicate_is_consulted_per_vendor() {
        var ids = NudgeableIds(HarnessCatalogTests.DetectionWithOnly("gemini", "antigravity"),
            id => id == "gemini", new HarnessOfferLedger());
        await Assert.That(ids).IsEquivalentTo(new[] { "antigravity" });
    }
}
