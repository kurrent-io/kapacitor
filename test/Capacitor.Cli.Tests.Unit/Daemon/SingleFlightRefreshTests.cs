using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// The coalescing/serialisation guarantees the background capability refresh depends on.
///
/// <para>These exist because a bare fire-and-forget refresh reintroduced the defect its own PR was
/// removing: concurrent rejected launches each started a refresh, and a slow FAILING one could
/// complete after a fast SUCCEEDING one and overwrite valid capabilities with a failed-probe null —
/// durably disabling the reviewer again. Atomic reference assignment prevents a torn pointer, not
/// stale completion order.</para>
/// </summary>
public class SingleFlightRefreshTests {
    // The exact interleaving from the review: a slow failing pass must not publish after a later
    // fast successful one. With single-flight, the second request never runs concurrently at all —
    // it folds into a rerun that happens AFTER the first finishes, so the last write is the newest.
    [Test] public async Task A_slow_pass_cannot_publish_after_a_later_request() {
        var refresh   = new SingleFlightRefresh();
        var published = new List<string>();
        var release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pass      = 0;

        var slow = refresh.RequestAsync(async () => {
            var n = Interlocked.Increment(ref pass);
            if (n == 1) { started.SetResult(); await release.Task; }
            lock (published) published.Add(n == 1 ? "slow-failing" : "fresh");
        });

        await started.Task;                     // first pass is in flight
        await refresh.RequestAsync(() => Task.CompletedTask);  // arrives mid-flight -> folds in
        release.SetResult();
        await slow;

        // Two passes ran, and the LAST one is the fresh recomputation — never the stale first.
        await Assert.That(published).IsEquivalentTo(new[] { "slow-failing", "fresh" });
    }

    // A burst of rejections must not queue a refresh each. N concurrent requests during one
    // in-flight pass collapse to exactly ONE extra pass.
    [Test] public async Task A_burst_of_requests_collapses_to_one_extra_pass() {
        var refresh = new SingleFlightRefresh();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passes  = 0;

        var first = refresh.RequestAsync(async () => {
            if (Interlocked.Increment(ref passes) == 1) { started.SetResult(); await release.Task; }
        });

        await started.Task;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => refresh.RequestAsync(() => Task.CompletedTask)));
        release.SetResult();
        await first;

        await Assert.That(passes).IsEqualTo(2);   // the original + exactly one coalesced rerun
    }

    // Passes never overlap — that is what makes "last write wins" mean "newest wins".
    [Test] public async Task Passes_never_run_concurrently() {
        var refresh    = new SingleFlightRefresh();
        var concurrent = 0;
        var maxSeen    = 0;

        await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => refresh.RequestAsync(async () => {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxSeen, now);
            await Task.Delay(5);
            Interlocked.Decrement(ref concurrent);
        })));

        await Assert.That(maxSeen).IsEqualTo(1);
    }

    // A refresh is a self-heal: a throw must never escape and become a second, different failure for
    // whatever triggered it — and must not wedge the gate against later refreshes.
    [Test] public async Task A_throwing_pass_is_reported_and_does_not_wedge_the_gate() {
        var refresh = new SingleFlightRefresh();
        Exception? observed = null;

        await refresh.RequestAsync(() => throw new InvalidOperationException("probe blew up"),
            ex => observed = ex);

        await Assert.That(observed).IsNotNull();
        await Assert.That(observed!.Message).IsEqualTo("probe blew up");

        // The gate still works afterwards.
        var ran = false;
        await refresh.RequestAsync(() => { ran = true; return Task.CompletedTask; });
        await Assert.That(ran).IsTrue();
    }

    // A request arriving mid-flight must earn a pass that STARTS after it — otherwise the refresh it
    // asked for could be one that began before the state it wanted observed.
    [Test] public async Task A_mid_flight_request_earns_a_pass_that_starts_after_it() {
        var refresh     = new SingleFlightRefresh();
        var release     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedAt = -1;
        var tick        = 0;
        var passStarts  = new List<int>();

        var first = refresh.RequestAsync(async () => {
            lock (passStarts) passStarts.Add(Interlocked.Increment(ref tick));
            if (passStarts.Count == 1) { started.SetResult(); await release.Task; }
        });

        await started.Task;
        requestedAt = Interlocked.Increment(ref tick);
        await refresh.RequestAsync(() => Task.CompletedTask);
        release.SetResult();
        await first;

        await Assert.That(passStarts.Count).IsEqualTo(2);
        await Assert.That(passStarts[1]).IsGreaterThan(requestedAt);
    }

    static void InterlockedMax(ref int target, int value) {
        int seen;
        do { seen = Volatile.Read(ref target); if (value <= seen) return; }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
