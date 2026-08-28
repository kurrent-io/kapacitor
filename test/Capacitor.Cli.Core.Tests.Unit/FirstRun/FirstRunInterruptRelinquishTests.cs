using Capacitor.Cli.Core.FirstRun;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

/// <summary>
/// The claim arbitration, and the process-global sink the CLI's signal handlers read. A leg reaches all of
/// this through <see cref="IFirstRunInterrupts"/>, so this is the only class that touches the static —
/// which is why it is the only one that needs assembly-wide exclusion.
/// </summary>
[NotInParallel]
public class FirstRunInterruptRelinquishTests {
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Test]
    public async Task An_armed_notice_is_sent_before_the_exit() {
        var sent = 0;

        using var notice = FirstRunInterruptRelinquish.Process.Arm(
            _ => { sent++; return Task.CompletedTask; });

        FirstRunInterruptRelinquish.RunBeforeExit(Budget);

        await Assert.That(sent).IsEqualTo(1);
    }

    [Test]
    public async Task A_disposed_notice_is_not_sent() {
        var sent = 0;

        FirstRunInterruptRelinquish.Process.Arm(_ => { sent++; return Task.CompletedTask; }).Dispose();

        FirstRunInterruptRelinquish.RunBeforeExit(Budget);

        await Assert.That(sent).IsEqualTo(0);
    }

    /// <summary>A handle going out of scope after a second leg armed must not take the second
    /// registration with it.</summary>
    [Test]
    public async Task Disposing_an_older_handle_leaves_the_newer_registration() {
        var first  = 0;
        var second = 0;

        var older = FirstRunInterruptRelinquish.Process.Arm(_ => { first++; return Task.CompletedTask; });
        using var newer = FirstRunInterruptRelinquish.Process.Arm(
            _ => { second++; return Task.CompletedTask; });

        older.Dispose();

        FirstRunInterruptRelinquish.RunBeforeExit(Budget);

        await Assert.That(first).IsEqualTo(0);
        await Assert.That(second).IsEqualTo(1);
    }

    // ---- the claim ----

    /// <summary>
    /// The whole point of the claim. Two paths want to send, and their reasons give opposite remedies, so a
    /// second send is a contradiction rather than a duplicate.
    /// </summary>
    [Test]
    public async Task Only_the_first_claimant_sends() {
        var sent = 0;

        using var notice = new FirstRunNotice(_ => { sent++; return Task.CompletedTask; });

        await notice.SendAsync(CancellationToken.None);
        notice.RunBeforeExit(Budget);

        await Assert.That(sent).IsEqualTo(1);
    }

    /// <summary>And in the other order, or "the second one is suppressed" would pass on the strength of the
    /// first path alone.</summary>
    [Test]
    public async Task An_interrupt_that_claims_first_suppresses_the_legs_own_send() {
        var sent = 0;

        using var notice = new FirstRunNotice(_ => { sent++; return Task.CompletedTask; });

        notice.RunBeforeExit(Budget);
        await notice.SendAsync(CancellationToken.None);

        await Assert.That(sent).IsEqualTo(1);
    }

    /// <summary>
    /// The loser waits instead of exiting through the winner. Without it an interrupt arriving mid-POST
    /// calls <c>Environment.Exit</c> and the notice is lost altogether — the failure a read-then-disarm
    /// shape cannot avoid either way round.
    /// </summary>
    [Test]
    public async Task An_interrupt_that_lost_the_claim_waits_for_the_send_to_finish() {
        var release  = new TaskCompletionSource();
        var finished = false;

        using var notice = new FirstRunNotice(async _ => {
            await release.Task;

            finished = true;
        });

        // SendAsync claims synchronously before its first await, so by the time it hands back a Task the
        // claim is taken and the interrupt below is deterministically the loser.
        var sending = notice.SendAsync(CancellationToken.None);
        var waiting = Task.Run(() => notice.RunBeforeExit(Budget));

        release.SetResult();

        await sending;
        await waiting;

        await Assert.That(finished).IsTrue();
    }

    /// <summary>The budget is a ceiling on an exit the user already asked for, so a send that hangs must
    /// not hold the process.</summary>
    [Test]
    public async Task A_send_that_hangs_is_bounded_by_the_budget() {
        using var notice = new FirstRunNotice(token => Task.Delay(Timeout.Infinite, token));

        var began = Environment.TickCount64;

        notice.RunBeforeExit(TimeSpan.FromMilliseconds(200));

        // Generous against a loaded CI box; what it pins is that the wait is bounded at all.
        await Assert.That(Environment.TickCount64 - began).IsLessThan(5_000);
    }

    /// <summary>It runs microseconds before an exit, so a throw has nowhere to go and must not become the
    /// last thing the process does.</summary>
    [Test]
    public async Task A_send_that_throws_is_swallowed() {
        var ran = 0;

        using var notice = new FirstRunNotice(_ => { ran++; throw new InvalidOperationException("boom"); });

        notice.RunBeforeExit(Budget);

        // Both halves: the send was entered, and control came back here rather than out of the handler.
        await Assert.That(ran).IsEqualTo(1);
    }

    /// <summary>Disposing releases an interrupt waiting on a send that is never coming, so a leg that had
    /// nothing to say does not cost the exit its whole budget.</summary>
    [Test]
    public async Task Disposing_releases_an_interrupt_waiting_on_a_send_that_never_comes() {
        var notice = new FirstRunNotice(_ => Task.CompletedTask);

        await notice.SendAsync(CancellationToken.None);
        notice.Dispose();

        var began = Environment.TickCount64;

        notice.RunBeforeExit(TimeSpan.FromSeconds(30));

        await Assert.That(Environment.TickCount64 - began).IsLessThan(5_000);
    }
}
