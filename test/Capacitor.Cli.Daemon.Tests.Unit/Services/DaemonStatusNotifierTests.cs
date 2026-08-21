using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The generation counter behind DaemonStatus pushes (spec §4.2): version check and source
/// capture are atomic, Pulse is a broadcast, and a stale cursor returns synchronously —
/// a missed pulse can never strand a subscriber.
/// </summary>
public class DaemonStatusNotifierTests {
    [Test]
    public async Task Wait_returns_synchronously_when_version_is_already_beyond_seen() {
        var n = new DaemonStatusNotifier();
        n.Pulse();
        var t = n.WaitBeyondAsync(0, CancellationToken.None);
        await Assert.That(t.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task Pulse_wakes_a_waiter_captured_at_the_current_version() {
        var n = new DaemonStatusNotifier();
        var t = n.WaitBeyondAsync(n.Version, CancellationToken.None);
        await Assert.That(t.IsCompleted).IsFalse();
        n.Pulse();
        await t.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Pulse_is_a_broadcast_every_waiter_in_the_generation_wakes() {
        var n = new DaemonStatusNotifier();
        var seen = n.Version;
        var a = n.WaitBeyondAsync(seen, CancellationToken.None);
        var b = n.WaitBeyondAsync(seen, CancellationToken.None);
        n.Pulse();
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task A_fresh_wait_after_a_pulse_blocks_until_the_next_pulse() {
        var n = new DaemonStatusNotifier();
        n.Pulse();
        var t = n.WaitBeyondAsync(n.Version, CancellationToken.None);
        await Assert.That(t.IsCompleted).IsFalse();
        n.Pulse();
        await t.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Cancellation_aborts_one_wait_without_disturbing_other_waiters() {
        var n = new DaemonStatusNotifier();
        var seen = n.Version;
        using var cts = new CancellationTokenSource();
        var cancelled = n.WaitBeyondAsync(seen, cts.Token);
        var live      = n.WaitBeyondAsync(seen, CancellationToken.None);

        cts.Cancel();
        var threw = false;
        try { await cancelled; } catch (OperationCanceledException) { threw = true; }
        await Assert.That(threw).IsTrue();

        n.Pulse();
        await live.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
