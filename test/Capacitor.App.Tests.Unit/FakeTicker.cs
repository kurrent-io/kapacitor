using System.Reactive.Subjects;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// Subject-backed ITicker: MainWindowViewModel construction across this project needs SOME
/// ticker, and most tests never care whether it ticks at all (UiTickerTests owns the real
/// Interval/off-UI-thread hazard) — Tick() is here for the few that inject ticks directly.
sealed class FakeTicker : ITicker {
    public readonly Subject<long> Subject = new();
    public IObservable<long> Ticks => Subject;
    public void Tick(long n = 0) => Subject.OnNext(n);
}
