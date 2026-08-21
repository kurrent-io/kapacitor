using Avalonia;
using Avalonia.Headless;
using ReactiveUI;
using ReactiveUI.Avalonia;
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
        new(() => {
            var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
            // Defensive, decompiler-verified: ReactiveUI.Avalonia's UseReactiveUI() only applies
            // WithAvalonia()'s AvaloniaScheduler wiring by calling ReactiveUIBuilder.BuildApp()
            // when Avalonia.AppBuilder.HasBeenBuilt is still false at that point in the pipeline
            // — and in this headless test process that flag can already be true (MTP/Avalonia
            // test infra builds its own AppBuilder earlier), silently skipping the wiring. When
            // that happens RxSchedulers.MainThreadScheduler is left at ReactiveUI's own default
            // (System.Reactive.Concurrency.DefaultScheduler — a background/thread-pool
            // scheduler), NOT the real Avalonia dispatcher, and every ObserveOn(RxSchedulers.
            // MainThreadScheduler) call in the app would silently deliver off the UI thread —
            // reproduced directly: constructing a MainWindowViewModel/MainWindow and publishing
            // a Status transition crashed with Avalonia's VerifyAccess() thread-affinity check,
            // from a System.Reactive DefaultScheduler.LongRunning worker thread. Set it
            // explicitly here so the baseline scheduler outside WithImmediateRxScheduler is
            // ALWAYS the real one, regardless of what UseReactiveUI's internal gating decided.
            RxSchedulers.MainThreadScheduler = AvaloniaScheduler.Instance;
            return session;
        });

    public static Task<T> DispatchAsync<T>(Func<T> body) =>
        Session.Value.Dispatch(body, CancellationToken.None);

    public static Task DispatchAsync(Action body) =>
        Session.Value.Dispatch(() => { body(); return true; }, CancellationToken.None);

    /// Async-body variant: HeadlessUnitTestSession.Dispatch's Func&lt;Task&lt;T&gt;&gt; overload
    /// runs `body` on the UI thread and, if it doesn't complete synchronously, pumps a
    /// DispatcherFrame until it does — so an `await` inside `body` that captures the ambient
    /// SynchronizationContext (e.g. `await someService.DisposeAsync()`, no ConfigureAwait) posts
    /// its continuation back onto this same pumped loop instead of deadlocking, unlike a raw
    /// `.GetAwaiter().GetResult()` block on the UI thread.
    public static Task<T> DispatchAsync<T>(Func<Task<T>> body) =>
        Session.Value.Dispatch(body, CancellationToken.None);

    /// Pins RxSchedulers.MainThreadScheduler to an immediate System.Reactive IScheduler for
    /// the body and RESTORES the prior scheduler in finally (it is process-global). This is
    /// also the flavor pin: it only compiles if the scheduler IS a System.Reactive IScheduler
    /// consumed by ObserveOn — the spec's scheduler-identity acceptance. (ReactiveUI 23.2.28
    /// moved the ambient scheduler off the classic static `RxApp` type onto `RxSchedulers`;
    /// `RxApp` scheduler properties no longer exist in this ReactiveUI line.)
    public static async Task WithImmediateRxScheduler(Func<Task> body) {
        // Force the (lazy, process-wide) session to actually start BEFORE snapshotting "prior" —
        // the Session factory above is what pins RxSchedulers.MainThreadScheduler to the real
        // AvaloniaScheduler. If this were the FIRST scheduler-touching call in the whole test
        // run, capturing "prior" before that pin would snapshot whatever System.Reactive's
        // unconfigured default is instead, and the `finally` below would then "restore" the
        // global to that wrong value forever — corrupting every later test that assumes the
        // real dispatcher scheduler is live outside this method.
        _ = Session.Value;

        IScheduler prior = RxSchedulers.MainThreadScheduler;
        RxSchedulers.MainThreadScheduler = ImmediateScheduler.Instance;
        try { await body(); } finally { RxSchedulers.MainThreadScheduler = prior; }
    }
}
