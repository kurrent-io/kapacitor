using System.Reactive.Linq;
using ReactiveUI.Reactive;

namespace Capacitor.App.Services;

public interface ITicker {
    /// Shared 1 Hz heartbeat. HOT via Publish().RefCount(); ticks are delivered on the UI thread.
    /// Construct the production implementation ON the UI thread (App.StartAsync) — an off-UI-
    /// thread Observable.Interval subscription binds an orphan thread-local dispatcher that never
    /// ticks (a real production bug; see UiTicker's pipeline comment).
    IObservable<long> Ticks { get; }
}

/// ONE shared ticker for every row (spec §8): Publish().RefCount() makes it HOT, so all rows
/// observe the same Interval and tick in lockstep instead of each cold-subscribing its own.
public sealed class UiTicker : ITicker {
    // SubscribeOn is load-bearing. Rows are built inside DynamicData's Transform, which runs on
    // whatever thread mutates the SourceCache — in production DaemonClientService's socket pump
    // thread, never the UI thread. AvaloniaScheduler's non-zero-dueTime path does NOT marshal: it
    // calls DispatcherTimer.RunOnce, which resolves Dispatcher.CurrentDispatcher — a THREAD-LOCAL
    // lookup that, off the UI thread, silently constructs a brand-new dispatcher for that thread
    // which nothing ever pumps. Its Tick never fires, so the Interval produces no value at all and
    // every row's Uptime freezes at its seed forever, with no exception and no log. SubscribeOn
    // forces the subscription — and therefore Interval's first Schedule call — onto the real UI
    // dispatcher via AvaloniaScheduler's zero-dueTime Dispatcher.UIThread.Post path.
    //
    // No StartWith: a consuming row's immediate first value is its own OAPH's initialUptime
    // argument, so a StartWith would only re-emit the identical string. The scheduler is
    // captured NOW (Rx operators take a scheduler by value, not a live reference to
    // RxSchedulers.MainThreadScheduler) and RefCount defers the connection until a row subscribes —
    // which is what lets a test construct this ticker inside AvaloniaSession.WithImmediateRxScheduler
    // WITHOUT ever subscribing a row: subscribing an Interval under an immediate scheduler would
    // block/spin forever, since Interval never completes.
    public IObservable<long> Ticks { get; } = Observable
        .Interval(TimeSpan.FromSeconds(1), RxSchedulers.MainThreadScheduler)
        .SubscribeOn(RxSchedulers.MainThreadScheduler)
        .Publish()
        .RefCount();
}
