using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty.Unix;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Phase B (D4 §6.4): the shared "reap by exact identity" primitive used by the
/// <c>HandleStopAgent</c> PID-record fallback (T7), the kill-quarantine retry (T8), and the startup
/// <c>OrphanReaper</c> (T9). Kills a leftover child ONLY when its identity is proven — never on
/// ambiguity — and reports whether death is CONFIRMED (so the caller deletes the record / drains the
/// quarantine only then, per spec §6.4(2)).
/// <para><b>Tri-state identity (fail-closed).</b> The leader pid is classified against its expected
/// start token as one of: <c>Dead</c> (pid gone — or a Linux zombie, treated as gone), <c>Ours</c>
/// (alive, exact match), <c>Recycled</c> (alive, CONCLUSIVELY a different incarnation — same scheme,
/// different value), or <c>Ambiguous</c> (alive but token unreadable / foreign scheme). Only <c>Dead</c>
/// and <c>Recycled</c> are "confirmed gone"; <c>Ambiguous</c> SPARES (returns "not gone") so a
/// still-alive child is never dropped on an unreadable token.</para>
/// <para><b>PID-reuse safety.</b> The token is re-classified before EVERY signal and after every wait,
/// so a pid recycled anywhere in the TERM→grace→KILL window is seen as <c>Recycled</c> and no signal is
/// sent to the new occupant.</para>
/// <para><b>Descendant containment.</b> Signals go to the process GROUP (<c>kill(-pid)</c>; a hosted
/// agent is a forkpty session leader with pgid==pid, so its mcp children are in the group) WHILE the
/// live leader still anchors ownership — so a stubborn descendant is SIGKILLed alongside the leader we
/// have proven is ours. Once the leader is gone we do NOT signal the numeric pgid: an empty leader slot
/// no longer proves the group is our lineage (the pid could be reused by an unrelated session leader),
/// and "ambiguity never kills". A descendant that outlives a leader which exited on TERM before the
/// group SIGKILL is the deferred native-containment concern (Linux PDEATHSIG / Windows Job Object),
/// which kills descendants with the leader at the OS level.</para>
/// </summary>
internal static class ProcessReaper {
    static readonly TimeSpan GraceBeforeKill  = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ConfirmAfterKill = TimeSpan.FromSeconds(5);

    enum LeaderState { Dead, Ours, Recycled, Ambiguous }

    static LeaderState Classify(int pid, string expectedIdentity) {
        if (!ProcessIdentity.IsAlive(pid)) return LeaderState.Dead; // gone or a zombie (IsAlive is zombie-aware)

        return ProcessIdentity.MatchesTri(pid, expectedIdentity) switch {
            true  => LeaderState.Ours,
            false => LeaderState.Recycled,   // same scheme, different value → conclusively a different process
            _     => LeaderState.Ambiguous,  // unreadable / foreign scheme → can't prove → spare
        };
    }

    /// <summary>Record-regime reap. Returns true when the target is CONFIRMED gone (dead, killed +
    /// observed dead, or a proven identity mismatch — our agent's process is not there), false when it
    /// may still be alive (kill unconfirmed, SPARED for an unreadable env, or an uncomparable token).</summary>
    public static async Task<bool> ReapByRecordAsync(AgentPidRecord record, ILogger logger, CancellationToken ct) {
        var pid = record.Pid;

        switch (Classify(pid, record.StartIdentity)) {
            case LeaderState.Dead:      return true;   // our process is gone
            case LeaderState.Recycled:  return true;   // pid reused by an unrelated proc — ours is gone
            case LeaderState.Ambiguous:
                logger.LogWarning(
                    "ProcessReaper: identity uncomparable for pid {Pid} (agent {AgentId}) — sparing (ambiguity never kills)",
                    pid, record.AgentId);
                return false;
        }

        // Ours: the EXACT start token already proves this is the same incarnation recorded at spawn, and
        // a proven exact (pid, start-identity) match is SUFFICIENT authorization to kill on every
        // platform. On LINUX ONLY we additionally require the live process to still carry our
        // KCAP_AGENT_ID as defense-in-depth — on Linux env IS readable via /proc/{pid}/environ, so an
        // unreadable OR mismatched value → SPARE and retain the record (ownership can't be proven
        // strongly enough to kill, NOT that the process is gone; reporting confirmed-gone would strand a
        // live child and drop its durable tracking). We deliberately do NOT apply this env guard on
        // macOS: macOS redacts other processes' env, so requiring it would ALWAYS spare and defeat the
        // exact mac:{bootsessionuuid}:{p_uniqueid} incarnation identity that is the whole point of the
        // macOS record kill (M1-A eventual leader recovery). Windows has no scan path either. The safety
        // invariant is unweakened: only a proven exact identity match reaches here (Ambiguous/null still
        // SPARES above); Dead / a conclusive token recycle return confirmed-gone above.
        if (OperatingSystem.IsLinux()) {
            var envAgentId = ProcessIdentity.ReadAgentEnv(pid, "KCAP_AGENT_ID");
            if (envAgentId is null || !string.Equals(envAgentId, record.AgentId, StringComparison.Ordinal)) {
                logger.LogWarning(
                    "ProcessReaper: env unreadable/mismatched for pid {Pid} (agent {AgentId}) — sparing (ambiguity never kills)",
                    pid, record.AgentId);
                return false;
            }
        }

        return await KillConfirmAsync(pid, record.StartIdentity, record.AgentId, logger, ct);
    }

    /// <summary>Marker-regime reap of a recordless survivor. The caller (OrphanReaper) has already
    /// captured the process's start token BEFORE reading its env-marker triple and re-validated it after,
    /// so <paramref name="expectedIdentity"/> is bound to the same incarnation whose markers proved
    /// ownership — no recapture here (which could adopt a replacement after a mid-scan PID reuse).</summary>
    public static Task<bool> ReapByMarkerAsync(int pid, string expectedIdentity, string agentId, ILogger logger, CancellationToken ct)
        => KillConfirmAsync(pid, expectedIdentity, agentId, logger, ct);

    /// <summary>Kill-quarantine retry (spec §6.4(2a)): the entry is a KNOWN-ours process whose death
    /// wasn't confirmed at teardown. Kill it by EXACT identity and report confirmed-gone (drain) vs
    /// still-alive/uncomparable (retain for the next tick).</summary>
    public static Task<bool> ReapByIdentityAsync(int pid, string expectedIdentity, string agentId, ILogger logger, CancellationToken ct)
        => KillConfirmAsync(pid, expectedIdentity, agentId, logger, ct);

    /// <summary>SIGTERM the process group, wait <see cref="GraceBeforeKill"/>, then SIGKILL; confirm the
    /// leader is gone. Re-classifies before/after each step so a pid recycled mid-sequence is detected as
    /// "ours is gone" and never signalled. The group signal (TERM then KILL) is only ever sent while the
    /// leader is alive-and-ours, so a stubborn descendant dies alongside the proven leader; once the
    /// leader is gone we never signal the pgid. Returns true only on confirmed death (or proven recycle),
    /// false on unconfirmed/ambiguous.</summary>
    static async Task<bool> KillConfirmAsync(int pid, string expectedIdentity, string agentId, ILogger logger, CancellationToken ct) {
        try {
            switch (Classify(pid, expectedIdentity)) {
                case LeaderState.Dead:      return true;
                case LeaderState.Recycled:  return true;
                case LeaderState.Ambiguous: return false;
            }

            SignalGroup(pid, hard: false); // SIGTERM the group (leader is proven ours → group is our lineage)

            // Poll for a definitive verdict; a transient Ambiguous (a proven-ours leader momentarily
            // unreadable mid-death-transition) keeps polling instead of sparing — see PollForConfirmedDeathAsync.
            if (await PollForConfirmedDeathAsync(pid, expectedIdentity, GraceBeforeKill, ct)) return true;

            // Re-gate before the hard kill: never SIGKILL a recycled/unprovable pid — ambiguity still
            // spares before we escalate, so the safety invariant is unchanged.
            switch (Classify(pid, expectedIdentity)) {
                case LeaderState.Dead:      return true;
                case LeaderState.Recycled:  return true;
                case LeaderState.Ambiguous: return false;
            }

            SignalGroup(pid, hard: true); // SIGKILL the group while the leader is still proven ours

            // Poll for the guaranteed kill to be reflected (zombie reaped / pid gone), which under load can
            // exceed one tick. On Windows the hard signal is a no-op — the soft signal above already
            // tree-killed and the grace poll gave it the full window — so don't add a second long wait there.
            var postKillWindow = OperatingSystem.IsWindows() ? TimeSpan.FromMilliseconds(250) : ConfirmAfterKill;
            if (await PollForConfirmedDeathAsync(pid, expectedIdentity, postKillWindow, ct)) return true;

            logger.LogWarning(
                "ProcessReaper: pid {Pid} (agent {AgentId}) not confirmed gone within {Grace}s+{Kill}s — retained for next sweep",
                pid, agentId, GraceBeforeKill.TotalSeconds, postKillWindow.TotalSeconds);
            return false;
        } catch (OperationCanceledException) {
            return false;
        } catch (Exception ex) {
            logger.LogWarning(ex, "ProcessReaper: kill of pid {Pid} (agent {AgentId}) failed", pid, agentId);
            return false;
        }
    }

    /// <summary>Poll <see cref="Classify"/> every 250ms up to <paramref name="window"/> for a definitive
    /// gone verdict (Dead or a proven pid-recycle). Called only after ownership is proven and the kill
    /// signal sent, so a transient Ambiguous keeps polling (never spares here); returns false at window
    /// expiry so the caller retains the record for the next sweep.</summary>
    static async Task<bool> PollForConfirmedDeathAsync(int pid, string expectedIdentity, TimeSpan window, CancellationToken ct) {
        // Classify, then delay-and-reclassify until the window elapses — the final classify happens
        // AFTER the last delay (when waited >= window), so a death reflected during that last interval
        // is still confirmed rather than dropped.
        for (var waited = TimeSpan.Zero; ; waited += TimeSpan.FromMilliseconds(250)) {
            switch (Classify(pid, expectedIdentity)) {
                case LeaderState.Dead:     return true;
                case LeaderState.Recycled: return true;
            }

            if (waited >= window) return false;
            await Task.Delay(250, ct);
        }
    }

    /// <summary>SIGTERM/SIGKILL the target's process group (Unix; falls back to the bare pid when it
    /// isn't a group leader). Only ever called with the leader proven alive-and-ours, so the group is our
    /// lineage. On Windows there are no process groups, so the soft call tree-kills and the hard call is a
    /// no-op (the tree is already gone).</summary>
    static void SignalGroup(int pid, bool hard) {
        if (pid <= 0) return;

        if (OperatingSystem.IsWindows()) {
            if (!hard) {
                try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
                catch { /* gone / unkillable */ }
            }

            return;
        }

        var sig = hard ? UnixPtyInterop.SIGKILL : UnixPtyInterop.SIGTERM;
        if (UnixPtyInterop.kill(-pid, sig) != 0) UnixPtyInterop.kill(pid, sig); // -pid = the group; fall back to the pid
    }
}
