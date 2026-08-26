using Capacitor.Cli.Commands;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class HookBudgetTests {
    // Whatever a hook names; the budget knows nothing about which event it came from.
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    [Test]
    public async Task remaining_is_the_ceiling_less_what_has_elapsed_and_less_safety() {
        var time   = new FakeTimeProvider();
        var budget = new HookClock(time).Budget(TimeSpan.FromSeconds(15));

        await Assert.That(budget.Remaining).IsEqualTo(TimeSpan.FromSeconds(13.5));

        time.Advance(TimeSpan.FromSeconds(10));

        await Assert.That(budget.Remaining).IsEqualTo(TimeSpan.FromSeconds(3.5));
    }

    /// <summary>
    /// The elapsed time is the CLOCK's, not the budget's. A hook can only name its ceiling once it has
    /// recognised the event, by which point the config load, the spool drain and the stdin read are
    /// already spent — and a budget that started counting at that moment would be over-generous by
    /// exactly the part of a hook that is slowest.
    /// </summary>
    [Test]
    public async Task a_budget_taken_out_late_is_still_measured_from_the_clock() {
        var time  = new FakeTimeProvider();
        var clock = new HookClock(time);

        time.Advance(TimeSpan.FromSeconds(2));

        await Assert.That(clock.Budget(Ceiling).Remaining).IsEqualTo(TimeSpan.FromSeconds(1.5));
    }

    [Test]
    public async Task an_overrun_clamps_to_zero_rather_than_going_negative() {
        var time   = new FakeTimeProvider();
        var budget = new HookClock(time).Budget(Ceiling);

        time.Advance(TimeSpan.FromSeconds(100));

        await Assert.That(budget.Remaining).IsEqualTo(TimeSpan.Zero);
        await Assert.That(budget.UntilCeiling).IsEqualTo(TimeSpan.Zero);
    }

    // A hook's other deadlines are measured on Time. Handing back anything but the clock the
    // budget itself uses would time one hook run by two clocks.
    [Test]
    public async Task the_clock_it_was_built_with_is_the_one_it_hands_back() {
        var time = new FakeTimeProvider();

        await Assert.That(new HookClock(time).Budget(Ceiling).Time).IsSameReferenceAs(time);
    }

    /// <summary>
    /// The reserve is held back from WORK, not from the hard cap: a hook that has stopped starting
    /// work still has to spool what it collected and exit, and that is what the reserve is for.
    /// Arming the cap on <see cref="HookBudget.Remaining"/> too would spend the reserve on the cap
    /// instead — leaving the exit path racing a cancellation the moment work stopped.
    /// </summary>
    [Test]
    public async Task the_cap_keeps_the_reserve_that_work_gives_up() {
        var time   = new FakeTimeProvider();
        var budget = new HookClock(time).Budget(Ceiling);

        await Assert.That(budget.UntilCeiling - budget.Remaining).IsEqualTo(HookBudget.Safety);

        // And once work is out of budget the cap still has exactly the reserve left to spend.
        time.Advance(Ceiling - HookBudget.Safety);

        await Assert.That(budget.Remaining).IsEqualTo(TimeSpan.Zero);
        await Assert.That(budget.UntilCeiling).IsEqualTo(HookBudget.Safety);
    }

    // Both are armed on the injected clock: a plain CancellationTokenSource/Task.Delay timer would
    // sit on the real one and never fire while a test advances a fake.
    [Test]
    public async Task the_cap_fires_on_the_clock_the_budget_was_built_with() {
        var time   = new FakeTimeProvider();
        var budget = new HookClock(time).Budget(Ceiling);

        using var cts      = budget.CancelAtCeiling();
        var       deadline = budget.CeilingReached();

        await Assert.That(cts.IsCancellationRequested).IsFalse();
        await Assert.That(deadline.IsCompleted).IsFalse();

        time.Advance(Ceiling);

        await Assert.That(cts.IsCancellationRequested).IsTrue();
        await deadline;
    }
}
