using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class HarnessNudgeTests {
    static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    static HarnessOfferLedger LedgerWith(params (string Vendor, HarnessOfferEntry Entry)[] rows) =>
        new() { Vendors = rows.ToDictionary(r => r.Vendor, r => r.Entry, StringComparer.Ordinal) };

    static IReadOnlyList<HarnessId> NudgeableIds(
            HarnessId[] detected, HarnessId[]? wired = null, HarnessOfferLedger? ledger = null) =>
        [.. HarnessNudge
            .Nudgeable(TestHarnesses.All(detected, wired), ledger ?? new HarnessOfferLedger(), Now)
            .Select(h => h.Id)];

    [Test]
    public async Task Detected_unwired_never_offered_is_nudgeable() {
        var ids = NudgeableIds([HarnessId.Antigravity]);
        await Assert.That(ids).Contains(HarnessId.Antigravity);
    }

    [Test]
    public async Task Wired_vendor_is_not_nudgeable() {
        var ids = NudgeableIds([HarnessId.Antigravity], wired: [HarnessId.Antigravity]);
        await Assert.That(ids).IsEmpty();
    }

    [Test]
    public async Task Undetected_vendor_is_not_nudgeable() {
        var ids = NudgeableIds([]);
        await Assert.That(ids).IsEmpty();
    }

    [Test]
    public async Task Declined_vendor_is_not_nudgeable() {
        var ledger = LedgerWith(("antigravity", new HarnessOfferEntry { Declined = true }));
        var ids = NudgeableIds([HarnessId.Antigravity], ledger: ledger);
        await Assert.That(ids).IsEmpty();
    }

    [Test]
    public async Task Offered_within_floor_is_not_nudgeable() {
        var ledger = LedgerWith(("antigravity", new HarnessOfferEntry { LastOffered = Now.AddDays(-3) }));
        var ids = NudgeableIds([HarnessId.Antigravity], ledger: ledger);
        await Assert.That(ids).IsEmpty();
    }

    [Test]
    public async Task Offered_past_floor_is_nudgeable_again() {
        var ledger = LedgerWith(("antigravity", new HarnessOfferEntry { LastOffered = Now.AddDays(-8) }));
        var ids = NudgeableIds([HarnessId.Antigravity], ledger: ledger);
        await Assert.That(ids).Contains(HarnessId.Antigravity);
    }

    [Test]
    public async Task Multiple_detected_unwired_all_nudgeable_in_catalog_order() {
        var ids = NudgeableIds([HarnessId.Gemini, HarnessId.Antigravity]);
        // registry order is Gemini before Antigravity
        await Assert.That(ids).IsEquivalentTo(new[] { HarnessId.Gemini, HarnessId.Antigravity });
    }

    [Test]
    public async Task Wiring_is_read_per_vendor() {
        var ids = NudgeableIds([HarnessId.Gemini, HarnessId.Antigravity], wired: [HarnessId.Gemini]);
        await Assert.That(ids).IsEquivalentTo(new[] { HarnessId.Antigravity });
    }
}
