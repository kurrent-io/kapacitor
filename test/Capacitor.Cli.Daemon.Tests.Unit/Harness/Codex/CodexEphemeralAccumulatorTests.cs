using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>The per-item ephemeral accumulator (§2.4): deltas fold into cumulative content-so-far,
/// items are independent, and completion drops an item's transient state.</summary>
public class CodexEphemeralAccumulatorTests {
    [Test]
    public async Task Deltas_fold_into_cumulative_content() {
        var acc = new CodexEphemeralAccumulator();
        await Assert.That(acc.Accumulate("i1", "Hel")).IsEqualTo("Hel");
        await Assert.That(acc.Accumulate("i1", "lo, ")).IsEqualTo("Hello, ");
        await Assert.That(acc.Accumulate("i1", "world")).IsEqualTo("Hello, world");
    }

    [Test]
    public async Task Items_accumulate_independently() {
        var acc = new CodexEphemeralAccumulator();
        acc.Accumulate("i1", "alpha");
        acc.Accumulate("i2", "beta");
        await Assert.That(acc.Accumulate("i1", "-1")).IsEqualTo("alpha-1");
        await Assert.That(acc.Accumulate("i2", "-2")).IsEqualTo("beta-2");
        await Assert.That(acc.ActiveItems).IsEqualTo(2);
    }

    [Test]
    public async Task Complete_drops_the_items_transient_state() {
        var acc = new CodexEphemeralAccumulator();
        acc.Accumulate("i1", "first");
        acc.Complete("i1");
        await Assert.That(acc.ActiveItems).IsEqualTo(0);

        // A later delta for the same id starts a fresh buffer (a completed item never re-accumulates).
        await Assert.That(acc.Accumulate("i1", "again")).IsEqualTo("again");
    }
}
