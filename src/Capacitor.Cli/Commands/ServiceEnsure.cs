using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The decision <c>kcap daemon service ensure</c> makes from a fresh status read (spec §3.4):
/// which verb to run, or which fail-closed attention row to report when no verb is safe. Pure —
/// no I/O, no mutations — so the ladder is directly testable. Mirrors the wizard's step-7 matrix
/// reduced to the flow's needs; every ambiguous row fails closed to attention, never guessed.
/// </summary>
public enum EnsureAction {
    /// <summary>Service already running under a validated daemon pid — nothing to do.</summary>
    AlreadyEnabled,

    /// <summary>No unit: install (baking the consent-seed directive).</summary>
    Install,

    /// <summary>Unit present but stopped: start.</summary>
    Start,

    /// <summary>Nothing was mutated; <see cref="EnsureDecision.Reason"/> names the ambiguous row.</summary>
    Attention
}

/// <summary>One ladder classification. <see cref="Reason"/> is non-null only for
/// <see cref="EnsureAction.Attention"/> — a coded token naming the row, never prose.</summary>
public readonly record struct EnsureDecision(EnsureAction Action, string? Reason = null);

/// <summary>
/// Pure ladder classifier: from the same evidence <c>status --json</c> reads, decide install /
/// start / already-enabled, or fail closed. Precedence mirrors the wizard's step-7 matrix: an
/// unreadable probe, a live transaction, then the repair rows (orphan label, stale marker) ahead
/// of any mutation — never install/start into an ambiguous state.
/// </summary>
internal static class EnsureClassifier {
    public static EnsureDecision Classify(
            LabelProbe probe, ServiceState state, bool unitPresent,
            int? daemonPid, bool txnMarker, bool txnActive) {
        // Unreadable evidence is the same fail-closed as status --json's unknown state: an unknown
        // probe must never masquerade as the well-defined not_installed row.
        if (probe == LabelProbe.Unknown)
            return new EnsureDecision(EnsureAction.Attention, "status_unknown");

        // A held transaction is never mutated into — the wizard waits it out; a CLI verb reports
        // it and lets the flow retry.
        if (txnActive)
            return new EnsureDecision(EnsureAction.Attention, "txn_active");

        // A validated pid means the daemon is up; that is the flow's done state.
        if (state == ServiceState.Running && daemonPid is not null)
            return new EnsureDecision(EnsureAction.AlreadyEnabled);

        // State says installed but no unit file on disk — the label is orphaned. Repair, never a blind start.
        if (state == ServiceState.Installed && !unitPresent)
            return new EnsureDecision(EnsureAction.Attention, "orphan_label");

        // A stale marker precedes every mutation row, never a blind reinstall (§6a).
        if (txnMarker)
            return new EnsureDecision(EnsureAction.Attention, "stale_marker");

        // Running without a validated pid is unconfirmed, not success.
        if (state == ServiceState.Running)
            return new EnsureDecision(EnsureAction.Attention, "running_unconfirmed");

        if (unitPresent) return new EnsureDecision(EnsureAction.Start);

        if (state == ServiceState.NotInstalled) return new EnsureDecision(EnsureAction.Install);

        return new EnsureDecision(EnsureAction.Attention, "status_unknown");
    }
}

/// <summary>Maps a <see cref="RecoverySurface"/> to its machine-readable wire token.</summary>
public static class RecoverySurfaceTokens {
    public static string Token(RecoverySurface surface) => surface switch {
        RecoverySurface.Takeover   => "takeover",
        RecoverySurface.Reinstall  => "reinstall",
        RecoverySurface.Attention  => "attention",
        RecoverySurface.Storage    => "storage",
        _                          => "none",
    };
}

/// <summary>
/// Pure: the wire fields for an ensure failure — <c>recovery</c> (gate refusals only) and
/// <c>reason</c>. Kept separate from I/O so the JSON contract is testable without a real service
/// manager. A gate refusal maps its <c>start_gate_reason=</c> token through <see cref="ReasonRouting"/>
/// (takeover/reinstall/attention); drift is never auto-retried (attention); every other exit on a
/// verified run carries its <c>verify_*</c> token; a plain (non-launchd) failure carries a plain
/// token, never a <c>verify_*</c> one — the verify prefix must not claim a transaction that never ran.
/// </summary>
internal static class EnsureFailureMap {
    public static (string? Recovery, string? Reason) Map(
            int exit, StartGateReason? gateReason, bool verified) {
        if (exit == VerifyExit.StartGate) {
            var reason = gateReason is { } r ? ServiceVerify.GateReasonToken(r) : null;
            var surface = reason is not null
                ? ReasonRouting.ForStartGate(reason)
                : RecoverySurface.Attention;
            return (RecoverySurfaceTokens.Token(surface), reason);
        }

        if (exit == VerifyExit.StartGateDrift)
            return (RecoverySurfaceTokens.Token(RecoverySurface.Attention), null);

        // Not a gate refusal. Verified runs carry their verify_* token; plain runs never wear the
        // verify prefix (exit 1 is lock contention or a manager error, not a verify outcome).
        return (null, verified ? VerifyExitToken(exit) : "plain_failure");
    }

    static string VerifyExitToken(int exit) => exit switch {
        VerifyExit.Contended           => "verify_contended",
        VerifyExit.Viability           => "verify_viability",
        VerifyExit.BootoutUnknown      => "verify_bootout_unknown",
        VerifyExit.StopUnconfirmed     => "verify_stop_unconfirmed",
        VerifyExit.ReadinessTimeout    => "verify_readiness_timeout",
        VerifyExit.HelloValidation     => "verify_hello_validation",
        VerifyExit.RollbackBudget      => "verify_rollback_budget",
        VerifyExit.RestoreVerification => "verify_restore_verification",
        VerifyExit.StartGate           => "verify_start_gate",
        VerifyExit.StartGateDrift      => "verify_start_gate_drift",
        _                              => $"verify_unknown_{exit}",
    };
}

/// <summary>Pure renderer for the ensure result — kept separate from I/O so it's directly testable.</summary>
internal static class ServiceEnsureRender {
    public static string RenderJson(ServiceEnsureJson result) =>
        System.Text.Json.JsonSerializer.Serialize(result, ServiceJsonContext.Default.ServiceEnsureJson);
}
