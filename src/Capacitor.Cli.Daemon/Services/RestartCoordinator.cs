using Capacitor.Cli.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Outcome of a restart request, used to ack the control-socket caller honestly.</summary>
internal enum RestartRequestResult {
    /// <summary>The restart is firing now (daemon shutting down / respawning).</summary>
    Restarting,
    /// <summary>Accepted but deferred — the daemon is busy; it will restart when idle.</summary>
    Queued,
    /// <summary>A restart was attempted but failed to start; it will be retried.</summary>
    Failed,
    /// <summary>Foreground daemon — no auto-restart; the user must restart it manually.</summary>
    ManualRequired,
}

/// <summary>
/// Polls the running binary; a size/mtime change queues a restart-after-update that
/// fires the chosen <see cref="IRestartStrategy"/> the moment the daemon is idle
/// (no hosted agents, no in-flight eval). Also handles explicit requests from the
/// control socket. Self-detection is macOS/Linux only (the poll is a no-op on Windows,
/// where a running binary can't be replaced); the explicit request path works anywhere.
/// </summary>
internal sealed partial class RestartCoordinator : BackgroundService {
    readonly DaemonStore _store;
    readonly string      _name;
    readonly string  _version;
    readonly ILogger _logger;

    // Seams (assigned from DI in the production ctor; overridden directly in tests).
    internal Func<BinaryStat?> StatBinary = static () => null;
    internal Func<bool>        IsBusy     = static () => false;
    internal IRestartStrategy  Strategy;

    // _gate guards all mutable state below. RequestRestart runs on the control-socket
    // handler thread while the poll loop runs on the timer thread, so the flags must be
    // synchronized — plain fields would risk stale reads / lost updates.
    readonly Lock _gate = new();
    BinaryStat?   _baseline;
    bool          _pending;
    bool          _force;
    bool          _fired;

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    // Production constructor (DI).
    public RestartCoordinator(
            DaemonConfig config, AgentOrchestrator orchestrator,
            EvalContextCache evalCache, IRestartStrategy strategy, ILogger<RestartCoordinator> logger) {
        _store     = config.Store;
        _name      = config.Name;
        _version   = DaemonRunner.ResolveDaemonVersion();
        _logger    = logger;
        Strategy   = strategy;
        IsBusy     = () => orchestrator.ActiveCount > 0 || evalCache.Count > 0;
        StatBinary = StatProcessBinary;
    }

    RestartCoordinator(DaemonStore store, string daemonName, string version, IRestartStrategy strategy, ILogger logger) {
        _store = store; _name = daemonName; _version = version; Strategy = strategy; _logger = logger;
    }

    /// <summary>Test factory — bypasses DI; caller sets the seams.</summary>
    internal static RestartCoordinator ForTest(DaemonStore store, string daemonName, string version, IRestartStrategy strategy) =>
        new(store, daemonName, version, strategy, NullLogger.Instance);

    internal void PrimeBaseline() {
        lock (_gate) _baseline = StatBinary();
    }

    static BinaryStat? StatProcessBinary() {
        // Windows can't replace a running binary in place, so self-detection is a no-op
        // there: returning null means BinaryChanged() never sees a change. Explicit
        // restart requests still work (they set _pending directly). Keeping the OS check
        // here — rather than in Tick() — leaves the gate logic OS-agnostic and testable.
        if (OperatingSystem.IsWindows()) return null;

        try {
            var p = Environment.ProcessPath;
            if (p is null) return null;
            var fi = new FileInfo(p);
            return fi.Exists ? new BinaryStat(fi.Length, fi.LastWriteTimeUtc.Ticks) : null;
        } catch {
            return null; // transient (file being swapped mid-install)
        }
    }

    /// <summary>
    /// Explicit restart request from the control socket. <paramref name="force"/> bypasses
    /// the idle gate. Evaluates immediately (rather than waiting for the next poll tick) so
    /// a manual restart of an already-idle daemon is prompt, and returns the result so the
    /// caller can ack honestly.
    /// </summary>
    internal RestartRequestResult RequestRestart(bool force) {
        lock (_gate) {
            _pending = true;
            if (force) _force = true;

            if (!force) {
                DaemonRestartMarker.Write(_store, _name, new DaemonRestartMarker(_version, "requested", DateTimeOffset.UtcNow));
                LogQueued(_logger, "requested");
            }
        }

        return Evaluate();
    }

    /// <summary>One poll iteration (timer-driven; also the unit-test entry point).</summary>
    internal void Tick() => Evaluate();

    /// <summary>
    /// Detect a binary change (if not already pending), then fire the strategy if the gate
    /// allows. The fire decision claims <c>_fired</c> under the lock for single-fire safety,
    /// but the (potentially blocking) strategy runs outside the lock. A <see cref="RestartOutcome.Retry"/>
    /// un-claims so a failed respawn is retried on the next tick/request.
    /// </summary>
    RestartRequestResult Evaluate() {
        bool fire;

        lock (_gate) {
            if (_fired) return RestartRequestResult.Restarting; // already in progress

            if (!_pending) {
                var current = StatBinary();
                if (RestartDecision.BinaryChanged(_baseline, current)) {
                    _pending  = true;
                    _baseline = current;
                    DaemonRestartMarker.Write(_store, _name, new DaemonRestartMarker(_version, "self-detected", DateTimeOffset.UtcNow));
                    LogQueued(_logger, "self-detected");
                }
            }

            fire = RestartDecision.ShouldFire(_pending, IsBusy(), _force);
            if (fire) _fired = true; // claim before releasing the lock (single-fire)
        }

        if (!fire) return RestartRequestResult.Queued;

        var outcome = Strategy.Restart();

        if (outcome == RestartOutcome.Retry) {
            lock (_gate) _fired = false; // un-claim so a later tick/request retries
            return RestartRequestResult.Failed;
        }

        return outcome == RestartOutcome.NoOp
            ? RestartRequestResult.ManualRequired
            : RestartRequestResult.Restarting;
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        PrimeBaseline();
        // The successor of a previous restart: clear any stale marker, since our
        // running version now matches the on-disk binary.
        DaemonRestartMarker.Delete(_store, _name);

        using var timer = new PeriodicTimer(PollInterval);
        try {
            while (await timer.WaitForNextTickAsync(ct)) Tick();
        } catch (OperationCanceledException) { /* shutdown */ }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart-after-update queued ({Reason}); will apply when idle")]
    static partial void LogQueued(ILogger logger, string reason);
}
