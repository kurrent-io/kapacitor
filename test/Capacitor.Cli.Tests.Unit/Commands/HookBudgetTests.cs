using System.Diagnostics;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class HookBudgetTests {
    [Test]
    public async Task session_end_ceiling_is_larger_than_others() {
        await Assert.That(HookBudget.Ceiling("session-end")).IsGreaterThan(HookBudget.Ceiling("stop"));
    }

    [Test]
    public async Task remaining_is_ceiling_minus_elapsed_minus_safety_and_never_negative() {
        var start = Stopwatch.GetTimestamp();
        var rem   = HookBudget.Remaining(start, "session-end");
        // ~15s ceiling - ~0 elapsed - 1.5s safety
        await Assert.That(rem).IsGreaterThan(TimeSpan.FromSeconds(12));
        await Assert.That(rem).IsLessThanOrEqualTo(TimeSpan.FromSeconds(13.5));

        // A start far in the past clamps to zero, never negative.
        var old = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 100);
        await Assert.That(HookBudget.Remaining(old, "stop")).IsEqualTo(TimeSpan.Zero);
    }
}
