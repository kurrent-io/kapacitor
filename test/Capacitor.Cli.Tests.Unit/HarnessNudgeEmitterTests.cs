using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Tests.Unit;

public class HarnessNudgeEmitterTests {
    static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    static string? Fragment(
            TempConfigRoot config, HarnessId[] detected, HarnessId[]? wired = null, bool optedOut = false) =>
        HarnessNudgeEmitter.ResolveFragment(
            TestHarnesses.All(detected, wired), new(config.Root), optedOut, Now);

    [Test]
    public async Task Fragment_names_detected_unwired_vendor_and_install_command() {
        using var config = new TempConfigRoot();
        var f = Fragment(config, [HarnessId.Antigravity])!;
        await Assert.That(f).Contains("Antigravity");
        await Assert.That(f).Contains("kcap plugin install --antigravity");
        await Assert.That(f).Contains("kcap harness dismiss antigravity");
    }

    [Test]
    public async Task Fragment_null_when_opted_out() {
        using var config = new TempConfigRoot();
        await Assert.That(Fragment(config, [HarnessId.Antigravity], optedOut: true)).IsNull();
    }

    [Test]
    public async Task Fragment_null_when_nothing_nudgeable() {
        using var config = new TempConfigRoot();
        await Assert.That(Fragment(config, [HarnessId.Antigravity], wired: [HarnessId.Antigravity])).IsNull();
    }

    [Test]
    public async Task Fragment_folds_multiple_vendors() {
        using var config = new TempConfigRoot();
        var f = Fragment(config, [HarnessId.Gemini, HarnessId.Antigravity])!;
        await Assert.That(f).Contains("Gemini");
        await Assert.That(f).Contains("Antigravity");
    }

    [Test]
    public async Task Second_call_is_throttled_to_null() {
        using var config    = new TempConfigRoot();
        var       store     = new HarnessOfferStore(config.Root);
        var       harnesses = TestHarnesses.All([HarnessId.Antigravity]);
        var       first     = HarnessNudgeEmitter.ResolveFragment(harnesses, store, false, Now);
        var       second    = HarnessNudgeEmitter.ResolveFragment(harnesses, store, false, Now);
        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task Resolving_stamps_last_offered() {
        using var config = new TempConfigRoot();
        var       store  = new HarnessOfferStore(config.Root);
        HarnessNudgeEmitter.ResolveFragment(TestHarnesses.All([HarnessId.Antigravity]), store, false, Now);
        await Assert.That(store.Load().Entry(HarnessId.Antigravity)!.LastOffered).IsEqualTo(Now);
    }

    /// A vendor's own probe can throw — a permission-denied read of its config — and a nudge must
    /// never break the hook it rides on.
    [Test]
    public async Task Exception_in_a_vendor_probe_yields_null() {
        using var config = new TempConfigRoot();
        var exploding = TestHarnesses.Over(
            BinaryProbe.Searching(null),
            new TestHarness(HarnessId.Antigravity, "Antigravity", new HarnessSignals {
                Installed = () => throw new InvalidOperationException("boom"),
            }));

        var result = HarnessNudgeEmitter.ResolveFragment(exploding, new(config.Root), false, Now);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Notice_has_kcap_prefix_and_stop_asking_hint() {
        using var config = new TempConfigRoot();
        var n = HarnessNudgeEmitter.ResolveNotice(
            TestHarnesses.All([HarnessId.Antigravity]), new(config.Root), false, Now)!;
        await Assert.That(n).Contains("kcap: Antigravity detected");
        await Assert.That(n).Contains("kcap harness dismiss antigravity");
    }

    [Test]
    public async Task Combine_joins_both_when_present_and_passes_through_when_one_null() {
        await Assert.That(HarnessNudgeEmitter.Combine("a", "b")).IsEqualTo("a\n\nb");
        await Assert.That(HarnessNudgeEmitter.Combine("a", null)).IsEqualTo("a");
        await Assert.That(HarnessNudgeEmitter.Combine(null, "b")).IsEqualTo("b");
        await Assert.That(HarnessNudgeEmitter.Combine(null, null)).IsNull();
    }
}
