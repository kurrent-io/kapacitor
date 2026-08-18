using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

public class CommandTimingTests {
    [Test]
    public async Task Elapsed_ms_is_derived_from_stopwatch_ticks() {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        Thread.Sleep(15);

        var elapsed = CommandTiming.ElapsedMs(start);

        await Assert.That(elapsed >= 10).IsTrue();
        await Assert.That(elapsed < 5_000).IsTrue();
    }

    [Test]
    public async Task Elapsed_ms_is_never_negative() {
        await Assert.That(CommandTiming.ElapsedMs(System.Diagnostics.Stopwatch.GetTimestamp() + 1_000_000) >= 0).IsTrue();
    }

    // Neither test above would fail against a stub that always returns a constant 15: the sleep
    // test only asserts >= 10, and the negative-clamp test only asserts >= 0. This one calls
    // ElapsedMs with no sleep at all, so a real implementation reads near zero — well under the
    // 15ms the other test sleeps for — while a hardcoded-15 stub fails it outright.
    [Test]
    public async Task Elapsed_ms_with_no_sleep_is_near_zero() {
        var elapsed = CommandTiming.ElapsedMs(System.Diagnostics.Stopwatch.GetTimestamp());

        await Assert.That(elapsed < 15).IsTrue();
    }
}
