using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

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
}
