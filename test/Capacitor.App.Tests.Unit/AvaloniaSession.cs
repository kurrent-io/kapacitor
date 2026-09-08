using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using ReactiveUI.Primitives.Reactive.Concurrency;
using ReactiveUI.Reactive;
using ReactiveUI.Reactive.Builder;
using ReactiveUI.Avalonia.Reactive;
using System.Reactive.Concurrency;

namespace Capacitor.App.Tests.Unit;

/// One process-global headless Avalonia session shared by every UI-touching test. The
/// session AND RxSchedulers.MainThreadScheduler are process-wide, so every test using this
/// class must carry [NotInParallel("AvaloniaSession")].
internal static class AvaloniaSession {
    sealed class TestAppBuilder {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<Capacitor.App.App>()
                .UseReactiveUI(_ => { })
                .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    static readonly Lazy<HeadlessUnitTestSession> Session =
        new(static () => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// ReactiveUI's builder state is process-global and effectively one-shot: whatever ran first
    /// keeps its registrations, so every later test inherits an Avalonia scheduler bound to a
    /// dispatcher nothing pumps — no ObserveOn(RxSchedulers.MainThreadScheduler) ever delivers and
    /// the failure reads as a dead pipeline rather than a scheduler. Rebuilding on the UI thread
    /// per dispatch is what ReactiveUI.Avalonia's own headless harness does; assigning
    /// MainThreadScheduler by hand instead loses to the builder.
    static void RebuildReactiveUI() {
        ReactiveUIBuilder.ResetBuilderStateForTests();
        RxAppBuilder.CreateReactiveUIBuilder().WithAvalonia().WithCoreServices().BuildApp();
        // WithAvalonia registers AvaloniaScheduler.Instance, a static whose Dispatcher is captured
        // at type init — under a headless session that is not Dispatcher.UIThread, and rebuilding
        // cannot change it. Left alone, every ObserveOn(RxSchedulers.MainThreadScheduler) posts to
        // a dispatcher nothing pumps: no value is delivered and it reads as a dead pipeline.
        RxSchedulers.MainThreadScheduler = _immediatePinned
            ? ImmediateScheduler.Instance
            : new AvaloniaScheduler(Dispatcher.UIThread);
    }

    /// A rebuild happens on every dispatch, so WithImmediateRxScheduler's swap has to survive one
    /// whichever way the two are nested.
    static bool _immediatePinned;

    public static Task<T> DispatchAsync<T>(Func<T> body) =>
        Session.Value.Dispatch(() => { RebuildReactiveUI(); return body(); }, CancellationToken.None);

    public static Task DispatchAsync(Action body) =>
        Session.Value.Dispatch(() => { RebuildReactiveUI(); body(); return true; }, CancellationToken.None);

    /// Async-body variant: HeadlessUnitTestSession.Dispatch's Func&lt;Task&lt;T&gt;&gt; overload
    /// runs `body` on the UI thread and, if it doesn't complete synchronously, pumps a
    /// DispatcherFrame until it does — so an `await` inside `body` that captures the ambient
    /// SynchronizationContext (e.g. `await someService.DisposeAsync()`, no ConfigureAwait) posts
    /// its continuation back onto this same pumped loop instead of deadlocking, unlike a raw
    /// `.GetAwaiter().GetResult()` block on the UI thread.
    public static Task<T> DispatchAsync<T>(Func<Task<T>> body) =>
        Session.Value.Dispatch(() => { RebuildReactiveUI(); return body(); }, CancellationToken.None);

    /// Pins RxSchedulers.MainThreadScheduler to an immediate System.Reactive IScheduler for
    /// the body and RESTORES the prior scheduler in finally (it is process-global). This is
    /// also the flavor pin: it only compiles if the scheduler IS a System.Reactive IScheduler
    /// consumed by ObserveOn — the spec's scheduler-identity acceptance. (ReactiveUI 23.2.28
    /// moved the ambient scheduler off the classic static `RxApp` type onto `RxSchedulers`;
    /// `RxApp` scheduler properties no longer exist in this ReactiveUI line.)
    public static async Task WithImmediateRxScheduler(Func<Task> body) {
        // Start the process-wide session before snapshotting "prior": outside a dispatch nothing
        // has configured MainThreadScheduler yet, and the finally below would restore that
        // unconfigured default over the real one for every later test.
        _ = Session.Value;

        IScheduler prior = RxSchedulers.MainThreadScheduler;
        _immediatePinned = true;
        RxSchedulers.MainThreadScheduler = ImmediateScheduler.Instance;
        try { await body(); } finally {
            _immediatePinned = false;
            RxSchedulers.MainThreadScheduler = prior;
        }
    }

    /// The standard wrapper for tests that need BOTH pieces at once: WithImmediateRxScheduler
    /// nested INSIDE DispatchAsync. ObserveOn(RxSchedulers.MainThreadScheduler) projections need
    /// the immediate scheduler to apply synchronously, while any Dispatcher.UIThread.InvokeAsync
    /// hop needs the live pumped dispatcher loop DispatchAsync provides -- outside a Dispatch
    /// frame the headless session's worker thread is blocked and a queued InvokeAsync never runs
    /// (see TerminalTabViewModelTests' header comment for the full account).
    public static Task RunOnUiAsync(Func<Task> body) =>
        DispatchAsync(async () => {
            await WithImmediateRxScheduler(body);
            return true;
        });
}
