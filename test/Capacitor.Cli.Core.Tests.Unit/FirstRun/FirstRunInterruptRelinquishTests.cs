using Capacitor.Cli.Core.FirstRun;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

/// <summary>
/// The process-global sink itself. Everything else reaches it through <see cref="IFirstRunInterrupts"/>,
/// so this class is the only one that touches the static — which is why it is the only one that needs
/// assembly-wide exclusion.
/// </summary>
[NotInParallel]
public class FirstRunInterruptRelinquishTests {
    [Test]
    public async Task An_armed_callback_runs_before_the_exit() {
        var sent = 0;

        using var armed = FirstRunInterruptRelinquish.Arm(_ => { sent++; return Task.CompletedTask; });

        FirstRunInterruptRelinquish.RunBeforeExit(TimeSpan.FromSeconds(1));

        await Assert.That(sent).IsEqualTo(1);
    }

    [Test]
    public async Task A_disarmed_callback_is_not_run() {
        var sent = 0;

        FirstRunInterruptRelinquish.Arm(_ => { sent++; return Task.CompletedTask; }).Dispose();

        FirstRunInterruptRelinquish.RunBeforeExit(TimeSpan.FromMilliseconds(50));

        await Assert.That(sent).IsEqualTo(0);
    }

    /// <summary>A handler going out of scope after a second leg armed must not take the second
    /// registration with it.</summary>
    [Test]
    public async Task Disposing_an_older_handle_leaves_the_newer_registration() {
        var first  = 0;
        var second = 0;

        var older = FirstRunInterruptRelinquish.Arm(_ => { first++; return Task.CompletedTask; });
        using var newer = FirstRunInterruptRelinquish.Arm(_ => { second++; return Task.CompletedTask; });

        older.Dispose();

        FirstRunInterruptRelinquish.RunBeforeExit(TimeSpan.FromSeconds(1));

        await Assert.That(first).IsEqualTo(0);
        await Assert.That(second).IsEqualTo(1);
    }

    /// <summary>It runs microseconds before an exit, so a throw has nowhere to go and must not become the
    /// last thing the process does.</summary>
    [Test]
    public async Task A_callback_that_throws_is_swallowed() {
        var ran = 0;

        using var armed = FirstRunInterruptRelinquish.Arm(
            _ => { ran++; throw new InvalidOperationException("boom"); });

        FirstRunInterruptRelinquish.RunBeforeExit(TimeSpan.FromSeconds(1));

        // Both halves: the callback was entered, and control came back to here rather than out of the
        // signal handler.
        await Assert.That(ran).IsEqualTo(1);
    }

    /// <summary>The budget is a ceiling on an exit the user already asked for, so a callback that hangs
    /// must not hold the process.</summary>
    [Test]
    public async Task A_callback_that_hangs_is_bounded_by_the_budget() {
        using var armed = FirstRunInterruptRelinquish.Arm(token => Task.Delay(Timeout.Infinite, token));

        var began = Environment.TickCount64;

        FirstRunInterruptRelinquish.RunBeforeExit(TimeSpan.FromMilliseconds(200));

        // Generous against a loaded CI box; what it pins is that the wait is bounded at all.
        await Assert.That(Environment.TickCount64 - began).IsLessThan(5_000);
    }

    [Test]
    public async Task The_process_sink_arms_the_static() {
        var sent = 0;

        using var armed = FirstRunInterruptRelinquish.Process.Arm(
            _ => { sent++; return Task.CompletedTask; });

        FirstRunInterruptRelinquish.RunBeforeExit(TimeSpan.FromSeconds(1));

        await Assert.That(sent).IsEqualTo(1);
    }
}
