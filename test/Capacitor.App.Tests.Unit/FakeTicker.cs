using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// Subject-backed ITicker: MainWindowViewModel construction across this project needs SOME
/// ticker, and most tests never care whether it ticks at all (UiTickerTests owns the real
/// Interval/off-UI-thread hazard) — Tick() is here for the few that inject ticks directly.
sealed class FakeTicker : ITicker {
    public readonly Subject<long> Subject = new();
    public IObservable<long> Ticks => Subject;
    public void Tick(long n = 0) => Subject.OnNext(n);
}

/// Harmless ActivityViewModel for the many MainWindowViewModel construction sites that don't
/// exercise the Activity tab at all — ActivityViewModelTests owns its own behavior.
static class TestActivity {
    public static ActivityViewModel New() =>
        new(() => new ConsentLogReadResult([], true), () => "unused", new FakeTicker());
}
