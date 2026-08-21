using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Unit tests for the daemon-side proactive token-refresh tick. The loop wakes on a
/// low-frequency timer and asks the port to refresh the active profile's token if it is inside
/// the expiry window. Like the daemon heartbeat loop, it runs as an unobserved background Task,
/// so <see cref="TokenRefreshLoop.TickAsync"/> must be total — a throwing refresh must not kill
/// the loop, or proactive refresh silently stops and the session lapses after the next idle gap.
///
/// The loop also rate-limits refresh ATTEMPTS to at most one per configured interval, so a
/// token that stays inside the window every tick (a failing refresh, or a token whose lifetime
/// is shorter than the window) can't hammer the refresh endpoint once per tick.
///
/// Exercised through a fake <see cref="IProactiveTokenRefreshPort"/> and an injected clock so
/// no network, token store, or wall-clock delay is touched.
/// </summary>
public class TokenRefreshLoopTests {
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    sealed class FakePort : IProactiveTokenRefreshPort {
        public Func<Task<ProactiveRefreshOutcome>>? Handler { get; set; }
        public int                                  Calls;

        public Task<ProactiveRefreshOutcome> RefreshIfExpiringAsync() {
            Calls++;

            return Handler is null ? Task.FromResult(ProactiveRefreshOutcome.NotDue) : Handler();
        }
    }

    /// <summary>Minimal <see cref="ILogger"/> that records the rendered message and level of every entry.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    [Test]
    public async Task Tick_TokenInsideWindow_RefreshesAndLogsAtDebug() {
        var logger = new CaptureLogger();
        var port   = new FakePort { Handler = () => Task.FromResult(ProactiveRefreshOutcome.Refreshed) };
        var loop   = new TokenRefreshLoop(port, logger, Interval);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.Calls).IsEqualTo(1);
        await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Debug && e.Message.Contains("refreshed"));
        await Assert.That(logger.Entries).DoesNotContain(e => e.Level == LogLevel.Warning);
    }

    [Test]
    public async Task Tick_TokenStillValid_IsQuietNoWarning() {
        var logger = new CaptureLogger();
        var port   = new FakePort { Handler = () => Task.FromResult(ProactiveRefreshOutcome.NotDue) };
        var loop   = new TokenRefreshLoop(port, logger, Interval);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.Calls).IsEqualTo(1);
        await Assert.That(logger.Entries).DoesNotContain(e => e.Level == LogLevel.Warning);
    }

    [Test]
    public async Task Tick_RefreshFailed_LogsWarning() {
        var logger = new CaptureLogger();
        var port   = new FakePort { Handler = () => Task.FromResult(ProactiveRefreshOutcome.Failed) };
        var loop   = new TokenRefreshLoop(port, logger, Interval);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.Calls).IsEqualTo(1);
        await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Warning && e.Message.Contains("failed"));
    }

    [Test]
    public async Task Tick_PortThrows_DoesNotRethrowAndLogsWarning() {
        // The unobserved background loop must survive a faulting refresh (e.g. a genuine IO
        // fault reading the token file that TokenStore lets propagate).
        var logger = new CaptureLogger();
        var port   = new FakePort { Handler = () => Task.FromException<ProactiveRefreshOutcome>(new InvalidOperationException("disk gone")) };
        var loop   = new TokenRefreshLoop(port, logger, Interval);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.Calls).IsEqualTo(1);
        await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Warning && e.Message.Contains("faulted"));
    }

    [Test]
    public async Task Tick_Contended_IsQuietAndDoesNotBackOff() {
        // Lock contention (a peer holds the refresh lock) is not a refresh failure: no warning,
        // and the next tick is NOT rate-limited — contention is transient, so we retry promptly.
        var now    = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var logger = new CaptureLogger();
        var port   = new FakePort { Handler = () => Task.FromResult(ProactiveRefreshOutcome.Contended) };
        var loop   = new TokenRefreshLoop(port, logger, Interval, () => now);

        await loop.TickAsync(CancellationToken.None);
        await Assert.That(port.Calls).IsEqualTo(1);
        await Assert.That(logger.Entries).DoesNotContain(e => e.Level == LogLevel.Warning);

        now = now.AddMinutes(1);                            // well within the rate-limit interval
        await loop.TickAsync(CancellationToken.None);
        await Assert.That(port.Calls).IsEqualTo(2);         // not suppressed — contention doesn't arm the gate
    }

    [Test]
    public async Task Tick_OuterCancellation_DoesNotLogWarning() {
        // Outer cancellation = process shutdown. A cancellation surfacing from the refresh
        // during teardown must be treated as a clean exit, not a fault to warn about.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var logger = new CaptureLogger();
        var port   = new FakePort { Handler = () => Task.FromException<ProactiveRefreshOutcome>(new OperationCanceledException()) };
        var loop   = new TokenRefreshLoop(port, logger, Interval);

        await loop.TickAsync(cts.Token);

        await Assert.That(logger.Entries).DoesNotContain(e => e.Level == LogLevel.Warning);
    }

    [Test]
    public async Task Tick_AfterFailedRefresh_SuppressesAttemptsUntilIntervalElapses() {
        // A failed refresh leaves the token inside the window, so without rate-limiting the
        // next tick would re-hit the (dead/rotated) refresh endpoint 60s later — and every 60s
        // forever. The loop must back off for the interval before attempting again.
        var now    = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var logger = new CaptureLogger();
        var port   = new FakePort { Handler = () => Task.FromResult(ProactiveRefreshOutcome.Failed) };
        var loop   = new TokenRefreshLoop(port, logger, Interval, () => now);

        await loop.TickAsync(CancellationToken.None);      // attempt #1
        await Assert.That(port.Calls).IsEqualTo(1);

        now = now.AddMinutes(2);                           // still inside the 5-minute interval
        await loop.TickAsync(CancellationToken.None);
        await Assert.That(port.Calls).IsEqualTo(1);         // suppressed — no second endpoint hit

        now = now.AddMinutes(3);                           // interval (5 min total) has elapsed
        await loop.TickAsync(CancellationToken.None);
        await Assert.That(port.Calls).IsEqualTo(2);         // allowed to retry now
    }

    [Test]
    public async Task Tick_AfterSuccessfulRefresh_AlsoSuppressesNextAttempt() {
        // Guards the short-token / JwtExpiry-fallback storm: even a SUCCESSFUL refresh can leave
        // the new token back inside the window (lifetime <= window), so a success must also arm
        // the rate limiter — otherwise we'd refresh (and rotate the WorkOS refresh token) every
        // tick.
        var now    = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var logger = new CaptureLogger();
        var port   = new FakePort { Handler = () => Task.FromResult(ProactiveRefreshOutcome.Refreshed) };
        var loop   = new TokenRefreshLoop(port, logger, Interval, () => now);

        await loop.TickAsync(CancellationToken.None);
        await Assert.That(port.Calls).IsEqualTo(1);

        now = now.AddMinutes(2);
        await loop.TickAsync(CancellationToken.None);
        await Assert.That(port.Calls).IsEqualTo(1);         // suppressed within the interval
    }

    [Test]
    public async Task Tick_NotDue_DoesNotArmRateLimiter() {
        // A no-op tick isn't an attempt, so it must not gate the next tick — a healthy token
        // that later enters the window must be refreshed promptly, not held off by an interval.
        var now    = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var logger = new CaptureLogger();
        var outcome = ProactiveRefreshOutcome.NotDue;
        var port   = new FakePort { Handler = () => Task.FromResult(outcome) };
        var loop   = new TokenRefreshLoop(port, logger, Interval, () => now);

        await loop.TickAsync(CancellationToken.None);       // NotDue
        await Assert.That(port.Calls).IsEqualTo(1);

        outcome = ProactiveRefreshOutcome.Refreshed;
        now = now.AddMinutes(1);                            // token just entered the window
        await loop.TickAsync(CancellationToken.None);
        await Assert.That(port.Calls).IsEqualTo(2);         // not suppressed — attempted immediately
    }
}
