using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// The daemon-side usage delta converter (§2.4): cumulative app-server totals → per-event deltas, so
/// the additive usage pipeline neither double-counts nor mis-attributes across a model reroute. Covers
/// the first snapshot, steady deltas, a cumulative reset, and the two resume baseline modes.
/// </summary>
public class CodexUsageDeltaConverterTests {
    // (input, cached, cacheWrite, output, reasoning, total) — mirrors the app-server TokenUsageBreakdown.
    static CodexTokenUsage U(long input, long cached, long cacheWrite, long output, long reasoning, long total) =>
        new(input, cached, cacheWrite, output, reasoning, total);

    [Test]
    public async Task First_snapshot_emits_the_whole_total() {
        var c = new CodexUsageDeltaConverter();
        var d = c.Convert(U(10, 2, 1, 5, 1, 15));
        await Assert.That(d).IsEqualTo(U(10, 2, 1, 5, 1, 15));
    }

    [Test]
    public async Task Subsequent_snapshot_emits_the_component_wise_delta() {
        var c = new CodexUsageDeltaConverter();
        c.Convert(U(10, 2, 1, 5, 1, 15));
        var d = c.Convert(U(25, 5, 4, 12, 3, 37));
        await Assert.That(d).IsEqualTo(U(15, 3, 3, 7, 2, 22));
    }

    [Test]
    public async Task Equal_snapshot_emits_a_zero_delta() {
        var c = new CodexUsageDeltaConverter();
        c.Convert(U(10, 2, 1, 5, 1, 15));
        var d = c.Convert(U(10, 2, 1, 5, 1, 15));
        await Assert.That(d).IsEqualTo(U(0, 0, 0, 0, 0, 0));
    }

    [Test]
    public async Task A_lower_cumulative_total_is_treated_as_a_fresh_baseline() {
        var c = new CodexUsageDeltaConverter();
        c.Convert(U(100, 20, 10, 50, 10, 150));
        var d = c.Convert(U(8, 1, 1, 4, 0, 12)); // dropped → reset: contribute the whole new total
        await Assert.That(d).IsEqualTo(U(8, 1, 1, 4, 0, 12));

        var next = c.Convert(U(20, 3, 2, 9, 1, 29)); // deltas resume from the reset baseline
        await Assert.That(next).IsEqualTo(U(12, 2, 1, 5, 1, 17));
    }

    [Test]
    public async Task Exact_resume_baseline_makes_the_next_delta_exact() {
        var c = new CodexUsageDeltaConverter();
        c.Convert(U(10, 2, 1, 5, 1, 15));
        c.SetExactBaseline(U(40, 8, 4, 20, 4, 60)); // thread/read reported this cumulative total on resume
        var d = c.Convert(U(55, 11, 7, 27, 6, 82));
        await Assert.That(d).IsEqualTo(U(15, 3, 3, 7, 2, 22));
    }

    [Test]
    public async Task Fallback_baseline_consumes_the_next_snapshot_and_emits_nothing() {
        var c = new CodexUsageDeltaConverter();
        c.Convert(U(10, 2, 1, 5, 1, 15));
        c.BaselineOnNextNotification();

        var baseline = c.Convert(U(60, 12, 6, 30, 6, 90)); // consumed as baseline
        await Assert.That(baseline).IsNull();

        var d = c.Convert(U(75, 15, 9, 37, 8, 112)); // deltas resume against the fallback baseline
        await Assert.That(d).IsEqualTo(U(15, 3, 3, 7, 2, 22));
    }
}
