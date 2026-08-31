namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Monotonic per-agent activity clock (liveness-supervision spec §0/§1). One instance per launch — a
/// relaunch gets a fresh one — fed by PTY output chunks, ACP transcript envelopes, ACP turn
/// start/end, Antigravity's and Pi's own <c>agentActivity</c>-flagged advances, two independent
/// <c>LocalPermissionBridge</c> reviewer tool-call-hit sites, and (round-dispatch grace) a
/// successfully delivered <c>SendInput</c> — see <see cref="AgentOrchestrator.HandleSendInput"/>.
///
/// <para>READS are lock-guarded, not just writes: the sources above run on independent threads (a PTY
/// read loop, an ACP connection's read thread, an HTTP listener), and an unguarded property read can
/// race a source's non-atomic multi-field update. Channel/queue happens-before does not cover it.</para>
///
/// <para>Every duration comes from <see cref="TimeProvider.GetTimestamp"/>/<see cref="TimeProvider.GetElapsedTime(long)"/>,
/// NEVER <see cref="TimeProvider.GetUtcNow"/> or a <see cref="DateTime"/> delta — a wall-clock step
/// (NTP, DST, a debugger pause) must not change <see cref="IdleForMs"/>, which is what makes a status
/// report a proof rather than a snapshot.</para>
/// </summary>
internal sealed class AgentActivityClock(TimeProvider time) {
    readonly Lock _gate = new();

    // Captured once at construction, on the same monotonic axis as _lastAdvanceTimestamp — never
    // re-read or reset. Backs AgeMs, the reaper's TTL input.
    readonly long _spawnTimestamp       = time.GetTimestamp();
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

    /// <summary>Fires the out-of-cycle status report (spec §1). Invoked OUTSIDE <see cref="_gate"/>,
    /// exactly once per GENUINE <see cref="SetLaunchStage"/> transition — never on a same-value re-set,
    /// and never from the other mutators; at most 4 per launch, so no cadence concern. Settable
    /// post-construction because the clock exists before the runtime that stamps stages does.</summary>
    public Action? OnLaunchStageChanged { get; set; }

    /// <summary>Monotonic elapsed time since the last <see cref="Advance"/>, sampled fresh on every
    /// read — never a value stamped once and reused. Spec §0's confirmed-idle rule depends on that:
    /// a status report attests silence measured AT report creation.</summary>
    public ulong IdleForMs {
        get {
            lock (_gate) {
                var elapsed = time.GetElapsedTime(_lastAdvanceTimestamp);

                return elapsed <= TimeSpan.Zero ? 0UL : (ulong) elapsed.TotalMilliseconds;
            }
        }
    }

    /// <summary>Monotonic elapsed time since this clock was constructed — never reset by any mutator,
    /// unlike <see cref="IdleForMs"/>. Backs the legacy absolute-lifetime backstop
    /// (<see cref="Capacitor.Cli.Daemon.DaemonConfig.ReviewerMaxLifetime"/>) on the same monotonic
    /// domain as everything else here.</summary>
    public ulong AgeMs {
        get {
            lock (_gate) {
                var elapsed = time.GetElapsedTime(_spawnTimestamp);

                return elapsed <= TimeSpan.Zero ? 0UL : (ulong) elapsed.TotalMilliseconds;
            }
        }
    }

    /// <summary>All four observables read under ONE lock acquisition, so they describe the same
    /// instant. The individual properties above cannot give that: each takes and releases
    /// <see cref="_gate"/> on its own, so a reader sampling several of them can interleave with an
    /// <see cref="Advance"/> between any two and act on a mixture of before- and after-states. The
    /// reviewer reaper depends on exactly that atomicity — it decides from the idle/age/turn fields and
    /// fences its later claim on <see cref="ActivitySeq"/>, and a seq belonging to a different instant
    /// than the idle it was selected on is precisely the stale evidence the fence exists to reject.
    /// </summary>
    public ActivitySnapshot Snapshot() {
        lock (_gate) {
            return new ActivitySnapshot(
                _activitySeq, Elapsed(_lastAdvanceTimestamp), Elapsed(_spawnTimestamp), _turnInFlight);
        }
    }

    // Caller must hold _gate. Same clamp the IdleForMs/AgeMs properties apply.
    ulong Elapsed(long since) {
        var elapsed = time.GetElapsedTime(since);

        return elapsed <= TimeSpan.Zero ? 0UL : (ulong) elapsed.TotalMilliseconds;
    }

    /// <summary>Records one unit of activity: bumps <see cref="ActivitySeq"/> and resets the idle
    /// window to zero from this instant. Called from all six sources (see the class doc).</summary>
    public void Advance() {
        lock (_gate) AdvanceLocked();
    }

    /// <summary>Fired on the turn's FALLING edge only (in-flight true → false) — the moment the
    /// server needs an out-of-cycle report, since idleness starts here and the next periodic tick is
    /// up to 60s away. A turn start needs no report of its own: the delivered input already fires one.</summary>
    public Action? OnTurnEnded { get; set; }

    /// <summary>Turn start/end — also counts as activity, independent of accompanying envelope
    /// traffic.</summary>
    public void SetTurnInFlight(bool value) {
        bool ended;
        lock (_gate) {
            ended = _turnInFlight && !value;
            _turnInFlight = value;
            AdvanceLocked();
        }
        // Outside the lock, same rule as OnLaunchStageChanged: the callback's send reads this
        // clock's own (independently guarded) properties.
        if (ended) OnTurnEnded?.Invoke();
    }

    /// <summary>A handshake stage transition (spawned → initialized → session_created → model_set) —
    /// also counts as activity. The out-of-cycle report it fires is what keeps the worst evidence gap
    /// inside the server's rolling registration deadline.</summary>
    public void SetLaunchStage(string stage) {
        bool changed;
        lock (_gate) {
            changed = _launchStage != stage;
            _launchStage = stage;
            AdvanceLocked();
        }
        // Outside the lock: the callback's send reads this clock's own (independently guarded)
        // properties, so holding _gate across it would deadlock a callback that reads back in.
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

/// <summary>One <see cref="AgentActivityClock"/> reading — all four fields from the same instant.
/// See <see cref="AgentActivityClock.Snapshot"/> for why sampling the properties separately is not
/// equivalent.</summary>
internal readonly record struct ActivitySnapshot(
    ulong ActivitySeq, ulong IdleForMs, ulong AgeMs, bool TurnInFlight);
