using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class HarnessOfferStoreTests {
    static HarnessOfferStore StoreIn(TempDir tmp) =>
        new(tmp.PathTo("harness-offers-v1.json"), tmp.PathTo("harness-offers.last-check"));

    [Test]
    public async Task Missing_file_loads_empty_ledger() {
        using var tmp = new TempDir();
        var ledger = StoreIn(tmp).Load();
        await Assert.That(ledger.Version).IsEqualTo(1);
        await Assert.That(ledger.Vendors).IsEmpty();
    }

    [Test]
    public async Task Corrupt_file_loads_empty_ledger() {
        using var tmp = new TempDir();
        tmp.CreateFile(["harness-offers-v1.json"], "{ this is not json ");
        var ledger = StoreIn(tmp).Load();
        await Assert.That(ledger.Vendors).IsEmpty();
    }

    [Test]
    public async Task Save_then_load_round_trips_entry() {
        using var tmp = new TempDir();
        var store = StoreIn(tmp);
        var when = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

        store.Save(new HarnessOfferLedger {
            Vendors = { ["antigravity"] = new HarnessOfferEntry { FirstSeen = when, LastOffered = when, Declined = true } }
        });

        var entry = store.Load().Entry("antigravity")!;
        await Assert.That(entry.Declined).IsTrue();
        await Assert.That(entry.LastOffered).IsEqualTo(when);
        await Assert.That(entry.FirstSeen).IsEqualTo(when);
    }

    [Test]
    public async Task Update_mutates_and_persists() {
        using var tmp = new TempDir();
        var store = StoreIn(tmp);

        store.Update(l => l with {
            Vendors = new(l.Vendors) { ["kiro"] = new HarnessOfferEntry { Declined = true } }
        });

        await Assert.That(store.Load().Entry("kiro")!.Declined).IsTrue();
    }

    [Test]
    public async Task TryClaimCheck_claims_once_then_blocks_within_window() {
        using var tmp = new TempDir();
        var store = StoreIn(tmp);

        await Assert.That(store.TryClaimCheck(TimeSpan.FromHours(6))).IsTrue();
        await Assert.That(store.TryClaimCheck(TimeSpan.FromHours(6))).IsFalse();
    }

    [Test]
    public async Task TryClaimCheck_zero_throttle_always_claims() {
        using var tmp = new TempDir();
        var store = StoreIn(tmp);

        await Assert.That(store.TryClaimCheck(TimeSpan.Zero)).IsTrue();
        await Assert.That(store.TryClaimCheck(TimeSpan.Zero)).IsTrue();
    }

    static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task StampOffered_sets_last_offered_and_first_seen() {
        using var tmp = new TempDir();
        var store = StoreIn(tmp);

        store.StampOffered(["antigravity"], Now);

        var entry = store.Load().Entry("antigravity")!;
        await Assert.That(entry.LastOffered).IsEqualTo(Now);
        await Assert.That(entry.FirstSeen).IsEqualTo(Now);
        await Assert.That(entry.Declined).IsFalse();
    }

    [Test]
    public async Task StampOffered_preserves_earlier_first_seen() {
        using var tmp = new TempDir();
        var store = StoreIn(tmp);
        var earlier = Now.AddDays(-30);

        store.Save(new HarnessOfferLedger { Vendors = { ["kiro"] = new HarnessOfferEntry { FirstSeen = earlier, LastOffered = earlier } } });
        store.StampOffered(["kiro"], Now);

        var entry = store.Load().Entry("kiro")!;
        await Assert.That(entry.FirstSeen).IsEqualTo(earlier);
        await Assert.That(entry.LastOffered).IsEqualTo(Now);
    }

    // The subtle invariant: an explicit dismissal must survive a later offer (e.g. re-running setup),
    // or a user's "stop asking" would be silently revived.
    [Test]
    public async Task StampOffered_never_overwrites_an_existing_dismissal() {
        using var tmp = new TempDir();
        var store = StoreIn(tmp);

        store.Save(new HarnessOfferLedger { Vendors = { ["cursor"] = new HarnessOfferEntry { Declined = true } } });
        store.StampOffered(["cursor"], Now);

        var entry = store.Load().Entry("cursor")!;
        await Assert.That(entry.Declined).IsTrue();
        await Assert.That(entry.LastOffered).IsNull();
    }
}
