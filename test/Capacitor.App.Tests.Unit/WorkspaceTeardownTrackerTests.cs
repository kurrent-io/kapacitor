using System.Collections.Concurrent;
using Capacitor.App.Services;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// Plain async tests — the tracker has no UI/Avalonia affinity, so no AvaloniaSession is needed.
/// The 5s-bound test uses TimerCountingTimeProvider (shared from ConsentServiceTests.cs) to know
/// the drain's Task.Delay is armed before advancing the underlying FakeTimeProvider — advancing
/// first would leave the timer scheduled into a future that already passed.
public class WorkspaceTeardownTrackerTests {
    static readonly DateTimeOffset T0 = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Track_runs_and_observes_a_normal_teardown() {
        var tracker = new WorkspaceTeardownTracker(new FakeTimeProvider(T0));
        var ran = false;

        tracker.Track(() => { ran = true; return Task.CompletedTask; });
        await tracker.DrainAsync();

        await Assert.That(ran).IsTrue();
    }

    [Test]
    public async Task Faulting_teardown_is_logged_exactly_once_via_diagnostics() {
        var diagnostics = new List<(string Context, Exception Ex)>();
        var tracker = new WorkspaceTeardownTracker(new FakeTimeProvider(T0), (ctx, ex) => diagnostics.Add((ctx, ex)));
        var boom = new InvalidOperationException("boom");

        tracker.Track(() => throw boom);
        await tracker.DrainAsync();

        await Assert.That(diagnostics.Count).IsEqualTo(1);
        await Assert.That(diagnostics[0].Ex).IsSameReferenceAs(boom);
    }

    [Test]
    public async Task Faulting_teardown_does_not_poison_the_drain_or_other_teardowns() {
        var diagnostics = new List<(string Context, Exception Ex)>();
        var tracker = new WorkspaceTeardownTracker(new FakeTimeProvider(T0), (ctx, ex) => diagnostics.Add((ctx, ex)));
        var otherRan = false;

        tracker.Track(() => throw new InvalidOperationException("boom"));
        tracker.Track(() => { otherRan = true; return Task.CompletedTask; });
        await tracker.DrainAsync();

        await Assert.That(diagnostics.Count).IsEqualTo(1);
        await Assert.That(otherRan).IsTrue();
    }

    [Test]
    public async Task Faulting_teardown_without_a_diagnostics_callback_does_not_throw() {
        var tracker = new WorkspaceTeardownTracker(new FakeTimeProvider(T0));

        tracker.Track(() => throw new InvalidOperationException("boom"));

        await tracker.DrainAsync(); // must not throw
    }

    [Test]
    public async Task DrainAsync_is_bounded_at_5_seconds_and_the_straggler_keeps_its_observer() {
        var innerTime = new FakeTimeProvider(T0);
        var countingTime = new TimerCountingTimeProvider(innerTime);
        var diagnostics = new List<(string Context, Exception Ex)>();
        var tracker = new WorkspaceTeardownTracker(countingTime, (ctx, ex) => diagnostics.Add((ctx, ex)));

        var gate = new TaskCompletionSource();
        tracker.Track(() => gate.Task); // never completes on its own

        var drainTask = tracker.DrainAsync();
        await WaitUntilAsync(() => countingTime.TimersCreated >= 1, what: "the 5s drain bound to be armed");
        innerTime.Advance(TimeSpan.FromSeconds(5));

        await drainTask; // returns without waiting for the straggler

        var straggler = new InvalidOperationException("late");
        gate.SetException(straggler);
        await WaitUntilAsync(() => diagnostics.Count == 1, what: "the straggler's fault to be logged");

        await Assert.That(diagnostics[0].Ex).IsSameReferenceAs(straggler);
    }

    [Test]
    public async Task DrainAsync_is_idempotent() {
        var tracker = new WorkspaceTeardownTracker(new FakeTimeProvider(T0));
        var runs = 0;
        tracker.Track(() => { runs++; return Task.CompletedTask; });

        var first = tracker.DrainAsync();
        await first;
        var second = tracker.DrainAsync(); // idempotent: must not throw, must not re-drain
        await second;

        await Assert.That(runs).IsEqualTo(1);
        await Assert.That(second).IsSameReferenceAs(first);
    }

    [Test]
    public async Task Track_racing_DrainAsync_runs_every_teardown_exactly_once() {
        var tracker = new WorkspaceTeardownTracker(new FakeTimeProvider(T0));
        var counts = new ConcurrentDictionary<int, int>();
        const int total = 2000;

        var producer = Task.Run(() => {
            for (var i = 0; i < total; i++) {
                var idx = i;
                tracker.Track(() => {
                    counts.AddOrUpdate(idx, 1, (_, c) => c + 1);
                    return Task.CompletedTask;
                });
            }
        });

        var drainTask = tracker.DrainAsync(); // races the producer loop above
        await Task.WhenAll(producer, drainTask);

        await Assert.That(counts.Count).IsEqualTo(total);
        await Assert.That(counts.Values.All(c => c == 1)).IsTrue();
    }
}
