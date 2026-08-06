// src/Capacitor.Cli.Daemon/Services/AgentActivityClock.cs
namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Monotonic per-agent activity clock (liveness-supervision spec §0/§1). One instance lives for the
/// lifetime of one launch (a relaunch gets a fresh instance — see <c>AgentInstance.ActivityClock</c>),
/// fed by four independent daemon-local sources: PTY output chunks, ACP transcript envelopes, ACP turn
/// start/end, and <c>LocalPermissionBridge</c> reviewer tool-call hits.
///
/// Thread-safe: every source above can fire concurrently (a PTY read loop, an ACP connection's single
/// read thread, and an HTTP listener thread are all independent), so every mutation AND every read
/// takes the same lock — a caller on one thread must be able to trust a value read the instant after a
/// source on another thread reports having advanced it (e.g. reading an envelope off a channel proves
/// the write that unblocked the read already happened-before, but proves nothing about a plain,
/// unguarded property read racing that same write's non-atomic multi-field update).
///
/// All durations are derived from <see cref="TimeProvider.GetTimestamp"/>/<see cref="TimeProvider.GetElapsedTime(long)"/>
/// (the monotonic clock domain) — NEVER from <see cref="TimeProvider.GetUtcNow"/> or <see cref="DateTime"/>
/// deltas. This is the load-bearing invariant a status report relies on to be a *proof* rather than a
/// snapshot (spec §0): a wall-clock step (NTP correction, DST, a debugger pause resuming the process)
/// must never change <see cref="IdleForMs"/>.
/// </summary>
internal sealed class AgentActivityClock(TimeProvider time) {
    readonly Lock _gate = new();

    long    _lastAdvanceTimestamp = time.GetTimestamp();
    ulong   _activitySeq = 1;
    bool    _turnInFlight;
    string? _launchStage;

    /// <summary>Starts at 1 on spawn (spec §0) — a freshly-launched agent is never "already idle";
    /// the daemon's own first report always shows real activity, never a zero-evidence agent.</summary>
    public ulong ActivitySeq {
        get { lock (_gate) return _activitySeq; }
    }

    /// <summary>ACP turn gate held (PTY agents: always false — their output IS the activity signal, so
    /// no separate turn-gate concept applies to them).</summary>
    public bool TurnInFlight {
        get { lock (_gate) return _turnInFlight; }
    }

    /// <summary><c>Starting</c>-only stage stamp (spawned/initialized/session_created/model_set, per
    /// the ACP handshake — wired in a later task); null once the agent reaches Running. Deliberately
    /// excluded from the wire's "steady-state capable" group (spec §2) — its absence must never look
    /// like a lost capability.</summary>
    public string? LaunchStage {
        get { lock (_gate) return _launchStage; }
    }

    /// <summary>Fired synchronously, OUTSIDE <see cref="_gate"/>, exactly once per GENUINE
    /// <see cref="SetLaunchStage"/> transition — never on a same-value re-set, and never from
    /// <see cref="Advance"/>/<see cref="SetTurnInFlight"/>/<see cref="ClearLaunchStage"/>. This is the
    /// daemon's hook for the immediate out-of-cycle status report (design §1): at most 4 per launch
    /// (one per handshake stage), so there is no cadence concern. Settable post-construction because
    /// the clock is built (and this callback wired, by <c>AgentOrchestrator.CreateActivityClock</c>)
    /// before the <c>AgentInstance</c>/ACP runtime that will eventually call <see cref="SetLaunchStage"/>
    /// exists.</summary>
    public Action? OnLaunchStageChanged { get; set; }

    /// <summary>
    /// Monotonic elapsed time since the last <see cref="Advance"/>, computed NOW (i.e. at report-
    /// creation time, whenever a caller reads this) — never a value stamped once and reused. This is
    /// what lets a status report attest "a full bound of silence had already elapsed at report
    /// creation" (spec §0's confirmed-idle rule): the read itself takes a fresh monotonic sample.
    /// </summary>
    public ulong IdleForMs {
        get {
            lock (_gate) {
                var elapsed = time.GetElapsedTime(_lastAdvanceTimestamp);

                return elapsed <= TimeSpan.Zero ? 0UL : (ulong) elapsed.TotalMilliseconds;
            }
        }
    }

    /// <summary>Records one unit of activity: bumps <see cref="ActivitySeq"/> and resets the idle
    /// window to zero, measured from THIS instant on the monotonic clock. Called from every one of
    /// the four sources (PTY chunk, ACP envelope, ACP turn transition, permission-bridge hit).</summary>
    public void Advance() {
        lock (_gate) AdvanceLocked();
    }

    /// <summary>Turn start/end (spec §0/§1) — also counts as activity: a turn beginning or ending is
    /// itself evidence the reviewer is alive, independent of whatever envelope traffic accompanies
    /// it.</summary>
    public void SetTurnInFlight(bool value) {
        lock (_gate) {
            _turnInFlight = value;
            AdvanceLocked();
        }
    }

    /// <summary>A handshake stage transition (spawned → initialized → session_created → model_set) —
    /// also counts as activity, and is load-bearing for the registration arithmetic in a later task
    /// (an out-of-cycle report on every stage change keeps the worst evidence gap inside the rolling
    /// deadline).</summary>
    public void SetLaunchStage(string stage) {
        bool changed;
        lock (_gate) {
            changed = _launchStage != stage;
            _launchStage = stage;
            AdvanceLocked();
        }
        // Invoked outside the lock: OnLaunchStageChanged fires an out-of-cycle status-report send
        // (design §1), and that send itself reads this same clock's properties (each independently
        // lock-guarded) — holding _gate across the callback would just be needless contention, and
        // risks deadlock if a future callback ever read back into this instance.
        if (changed) OnLaunchStageChanged?.Invoke();
    }

    /// <summary>Clears the stage stamp once the agent reaches Running — its absence from then on is
    /// the steady-state, not a missing capability (spec §2).</summary>
    public void ClearLaunchStage() {
        lock (_gate) {
            _launchStage = null;
            AdvanceLocked();
        }
    }

    // Caller must hold _gate.
    void AdvanceLocked() {
        _activitySeq++;
        _lastAdvanceTimestamp = time.GetTimestamp();
    }
}
