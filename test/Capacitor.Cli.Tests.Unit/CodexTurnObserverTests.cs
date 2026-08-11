using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Codex turn-start diagnostic: tests the pure growth-observation core that turns a hosted Codex reviewer's rollout
/// into a turn-start signal. Growth ⇒ the reviewer began a turn; silence to the deadline ⇒ it
/// received the input but produced no turn; cancellation ⇒ the agent stopped. The length source
/// and clock are injected, so no filesystem or wall-clock is involved.
/// </summary>
public class CodexTurnObserverTests {
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    static readonly TimeSpan Poll    = TimeSpan.FromSeconds(2);

    [Test]
    public async Task Growth_before_first_poll_is_observed_immediately() {
        // A fast reviewer that has already appended by the first check needs no polling at all.
        var outcome = await CodexTurnObserver.ObserveGrowthAsync(
            currentLength: () => 100, baseline: 10, Timeout, Poll, new FakeTimeProvider(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(CodexTurnObserver.Outcome.TurnObserved);
    }

    [Test]
    public async Task Growth_during_polling_is_observed() {
        var  time = new FakeTimeProvider();
        long len  = 10; // starts at the baseline — the first check must NOT credit a turn

        var task = CodexTurnObserver.ObserveGrowthAsync(() => len, baseline: 10, Timeout, Poll, time, CancellationToken.None);

        // The synchronous first check (len==baseline) already ran and armed the poll delay; now the
        // rollout grows and the clock advances past one interval, so the next check credits the turn.
        len = 42;
        time.Advance(Poll);

        await Assert.That(await task).IsEqualTo(CodexTurnObserver.Outcome.TurnObserved);
    }

    [Test]
    public async Task No_growth_until_timeout_is_not_observed() {
        var time = new FakeTimeProvider();

        var task = CodexTurnObserver.ObserveGrowthAsync(() => 10, baseline: 10, Timeout, Poll, time, CancellationToken.None);

        time.Advance(Timeout + Poll); // push past the deadline with the length unchanged

        await Assert.That(await task).IsEqualTo(CodexTurnObserver.Outcome.NotObserved);
    }

    [Test]
    public async Task Unreadable_length_at_deadline_is_unavailable_not_no_turn() {
        var time = new FakeTimeProvider();

        // The length source signals "unreadable" (negative) the whole time — a deleted/moved
        // rollout or sustained stat failure. This must NOT be reported as "no turn".
        var task = CodexTurnObserver.ObserveGrowthAsync(() => -1, baseline: 10, Timeout, Poll, time, CancellationToken.None);

        time.Advance(Timeout + Poll);

        await Assert.That(await task).IsEqualTo(CodexTurnObserver.Outcome.Unavailable);
    }

    [Test]
    public async Task Transient_unreadable_then_growth_is_still_observed() {
        var  time = new FakeTimeProvider();
        long len  = -1; // momentarily unreadable at the first check — must not terminate the probe

        var task = CodexTurnObserver.ObserveGrowthAsync(() => len, baseline: 10, Timeout, Poll, time, CancellationToken.None);

        len = 42; // the file becomes readable and has grown
        time.Advance(Poll);

        await Assert.That(await task).IsEqualTo(CodexTurnObserver.Outcome.TurnObserved);
    }

    [Test]
    public async Task Cancellation_is_reported() {
        var       time = new FakeTimeProvider();
        using var cts  = new CancellationTokenSource();

        var task = CodexTurnObserver.ObserveGrowthAsync(() => 10, baseline: 10, Timeout, Poll, time, cts.Token);

        await cts.CancelAsync();

        await Assert.That(await task).IsEqualTo(CodexTurnObserver.Outcome.Cancelled);
    }
}
