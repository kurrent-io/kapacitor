using System.Reactive.Linq;

namespace Capacitor.App.Tests.Unit;

public class AvaloniaSessionTests
{
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispatch_runs_on_the_headless_session()
    {
        var answer = await AvaloniaSession.DispatchAsync(() => 42);
        await Assert.That(answer).IsEqualTo(42);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Scheduler_swap_observes_on_the_immediate_scheduler_and_restores()
    {
        var prior = ReactiveUI.RxSchedulers.MainThreadScheduler;
        await AvaloniaSession.WithImmediateRxScheduler(async () =>
        {
            string? seen = null;
            using var _ = System.Reactive.Linq.Observable.Return("published-on-background")
                .ObserveOn(ReactiveUI.RxSchedulers.MainThreadScheduler)
                .Subscribe(v => seen = v);
            await Task.Yield();
            await Assert.That(seen).IsEqualTo("published-on-background"); // immediate scheduler delivered synchronously
        });
        await Assert.That(ReferenceEquals(ReactiveUI.RxSchedulers.MainThreadScheduler, prior)).IsTrue(); // restored
    }
}
