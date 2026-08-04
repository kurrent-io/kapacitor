// src/Capacitor.Cli.Daemon/Acp/AcpReconnect.cs
namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Everything an <c>AcpHostedAgentRuntime</c> needs to attempt crash reconnect/resume, supplied by
/// <c>AcpHostedAgentRuntimeFactory</c> ONLY for an eligible launch (probe-verified vendor,
/// interactive, kill switch off — reconnect spec §4). A runtime constructed without one of these
/// keeps today's behavior byte-for-byte: child death → read loop ends → finalize.
/// </summary>
internal sealed class AcpReconnectSupport {
    /// <summary>
    /// The pure spawn closure (spec §6.2): constructs a fresh child process + stdio streams for the
    /// SAME launch shape as the original (binary, argv, env, cwd) and nothing else — no agent
    /// registration, no forwarder, no slot accounting. The factory closes this over the launch's
    /// <c>RuntimeStartContext</c> and its own connection source, so candidates and the original
    /// child are spawned by the same code path.
    /// </summary>
    public required Func<(Stream Input, Stream Output, IAcpProcess Process)> Spawn { get; init; }

    /// <summary>Drives attempt backoff and the settlement-wait bound; injectable for test determinism.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// The durable PID-record callbacks, published as ONE immutable bundle (code-review r2: two
    /// independently-settable callbacks had a partial-wiring window — real recorder installed,
    /// clearer still absent — in which a failed attempt could persist a candidate record it could
    /// never clear). <c>Record</c> runs at candidate spawn, before any handshake, and MUST throw on
    /// failure — an unrecorded child may never proceed, or daemon-death leak containment is
    /// fiction; the runtime treats a throw as the attempt failing and disposes the candidate.
    /// <c>Clear</c> runs after a failed candidate's disposal and at incident terminalization,
    /// bounding the window in which a stale record names a dead pid. The FACTORY installs a
    /// fail-closed default (throwing Record, no-op Clear); the orchestrator replaces the whole
    /// bundle in one atomic reference assignment after registration. Tests install their own.
    /// </summary>
    public AcpPidRecordCallbacks PidCallbacks { get; set; } = AcpPidRecordCallbacks.Unwired;

    /// <summary>Delays BETWEEN the up-to-3 candidate spawns of one incident (spec §6: t=0, +1s, +4s).</summary>
    public IReadOnlyList<TimeSpan> AttemptDelays { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>Bounded wait for the old process tree's CONFIRMED exit during corpse retirement
    /// (spec §6.1 — unconfirmed exit is terminal for the incident, never shrugged past).</summary>
    public TimeSpan RetirementWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Bounded wait for the incident turn's settlement after commit (spec §6.4). Generous —
    /// the faulted turn's awaits were already faulted, so completion is prompt; a pathological hang
    /// goes terminal rather than waiting forever.</summary>
    public TimeSpan SettlementWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Lifetime cap of COUNTED resumes per session (spec §6.4's success linearization: a
    /// resume counts exactly when the atomic reopen commits). The next crash past the cap
    /// finalizes — a child that dies after every resume is a broken installation, not a transient.</summary>
    public int MaxResumesPerSession { get; init; } = 5;
}

/// <summary>The atomic PID-record callback bundle — see
/// <see cref="AcpReconnectSupport.PidCallbacks"/> for the contract and why it is one reference,
/// never two setters.</summary>
internal sealed record AcpPidRecordCallbacks(Action<int> Record, Action Clear) {
    /// <summary>The fail-closed pre-wiring state: recording throws (an attempt racing the
    /// orchestrator's wiring window fails honestly, per §6.2's record-before-any-handshake MUST),
    /// clearing is a no-op (there is no record to clear).</summary>
    public static readonly AcpPidRecordCallbacks Unwired = new(
        _ => throw new InvalidOperationException(
            "ACP reconnect: PID recorder not yet wired (crash raced launch registration)."),
        () => { });
}

/// <summary>
/// Thrown inside the reconnect owner when a condition is terminal for the whole INCIDENT (not just
/// the current attempt): a `session/load` JSON-RPC refusal (both measured refusal classes are
/// durable), a protocol downgrade, `loadSession` withdrawn, unconfirmed corpse retirement, or a
/// settlement-wait timeout. The owner stops attempting and finalizes.
/// </summary>
internal sealed class AcpReconnectTerminalException(string reason, string message) : Exception(message) {
    public string Reason { get; } = reason;
}
