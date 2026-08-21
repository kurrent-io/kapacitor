using Avalonia.Threading;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// Regression coverage for the frozen-Uptime bug (moved here from AgentGridTests, which used to
/// drive it through MainWindowViewModel's internal `Ticker` test seam): a row's shared ticker
/// used to be subscribed inside DynamicData's Transform, which runs on whatever thread mutates
/// the SourceCache — in production DaemonClientService's socket pump thread, never the UI thread.
/// Subscribing the shared ticker from there bound its DispatcherTimer to a per-thread dispatcher
/// nothing pumps, so the Interval produced no value at all and Uptime stayed at its seed forever.
/// This drives UiTicker's REAL ticker (real 1s Interval on the real AvaloniaScheduler) from a
/// background thread — the row-level uptime tests inject a Subject and are blind to the whole
/// hazard.
public class UiTickerTests {
    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Ticker_subscribed_off_the_ui_thread_still_ticks_on_the_dispatcher() {
        var (ticks, everyTickOnUiThread) = await AvaloniaSession.DispatchAsync(async () => {
            var ticker = new UiTicker(); // construct ON the UI thread, per the production contract

            var count = 0;
            var onUiThread = true;
            IDisposable? subscription = null;
            await Task.Run(() => subscription = ticker.Ticks.Subscribe(_ => {
                onUiThread &= Dispatcher.UIThread.CheckAccess();
                Interlocked.Increment(ref count);
            }));

            // Baseline excludes anything the ticker delivers SYNCHRONOUSLY on subscribe (a
            // StartWith-style seed), so the wait below can only be satisfied by a genuine periodic
            // tick — the exact thing the broken version never produced.
            var seedOnSubscribe = Volatile.Read(ref count);

            // DispatchAsync's async overload pumps a DispatcherFrame around this body, which is
            // what lets the dispatcher's own timers run while we wait.
            await WaitUntilAsync(() => Volatile.Read(ref count) > seedOnSubscribe, what: "a periodic ticker tick");
            subscription!.Dispose();

            return (Volatile.Read(ref count) - seedOnSubscribe, onUiThread);
        });

        await Assert.That(ticks).IsGreaterThan(0);
        await Assert.That(everyTickOnUiThread).IsTrue();
    }
}
