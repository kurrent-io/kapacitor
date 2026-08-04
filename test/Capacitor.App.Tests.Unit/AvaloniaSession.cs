using Avalonia;
using Avalonia.Headless;
using ReactiveUI;
using ReactiveUI.Avalonia;
using System.Reactive.Concurrency;

namespace Capacitor.App.Tests.Unit;

/// One process-global headless Avalonia session shared by every UI-touching test. The
/// session AND RxSchedulers.MainThreadScheduler are process-wide, so every test using this
/// class must carry [NotInParallel("AvaloniaSession")].
internal static class AvaloniaSession
{
    sealed class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<Capacitor.App.App>()
                .UseReactiveUI(_ => { })
                .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

    public static Task<T> DispatchAsync<T>(Func<T> body) =>
        Session.Value.Dispatch(body, CancellationToken.None);

    public static Task DispatchAsync(Action body) =>
        Session.Value.Dispatch(() => { body(); return true; }, CancellationToken.None);

    /// Pins RxSchedulers.MainThreadScheduler to an immediate System.Reactive IScheduler for
    /// the body and RESTORES the prior scheduler in finally (it is process-global). This is
    /// also the flavor pin: it only compiles if the scheduler IS a System.Reactive IScheduler
    /// consumed by ObserveOn — the spec's scheduler-identity acceptance. (ReactiveUI 23.2.28
    /// moved the ambient scheduler off the classic static `RxApp` type onto `RxSchedulers`;
    /// `RxApp` scheduler properties no longer exist in this ReactiveUI line.)
    public static async Task WithImmediateRxScheduler(Func<Task> body)
    {
        IScheduler prior = RxSchedulers.MainThreadScheduler;
        RxSchedulers.MainThreadScheduler = ImmediateScheduler.Instance;
        try { await body(); } finally { RxSchedulers.MainThreadScheduler = prior; }
    }
}
