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
/// unreadable probe, a live transaction, then the repair rows (orphan label, stale marker) —
/// ALL ahead of any success or mutation row, so an ambiguity can never ride a validated pid
/// into "already enabled" (the fail-closed rule the docs state unqualified).
/// </summary>
internal static class EnsureClassifier {
    public static EnsureDecision Classify(
            LabelProbe probe, ServiceState state, bool unitPresent,
            int? daemonPid, bool txnMarker, bool txnActive,
            int? jobPid = null, bool jobPidAvailable = false) {
        // Unreadable evidence is the same fail-closed as status --json's unknown state: an unknown
        // probe must never masquerade as the well-defined not_installed row.
        if (probe == LabelProbe.Unknown)
            return new EnsureDecision(EnsureAction.Attention, "status_unknown");

        // A held transaction is never mutated into — the wizard waits it out; a CLI verb reports
        // it and lets the flow retry.
        if (txnActive)
            return new EnsureDecision(EnsureAction.Attention, "txn_active");

        // The repair rows precede the success arm. A loaded label whose unit file is gone — whether
        // it reads Installed or Running — is an installation that cannot survive a relaunch: orphaned,
        // never "already enabled". A stale marker means a prior transaction never reached a terminal
        // state: ambiguous, never success.
        if ((state == ServiceState.Installed || state == ServiceState.Running) && !unitPresent)
            return new EnsureDecision(EnsureAction.Attention, "orphan_label");

        if (txnMarker)
            return new EnsureDecision(EnsureAction.Attention, "stale_marker");

        // "Already enabled" means the service job OWNS the validated daemon pid. On launchd the
        // manager reports the job's pid, and it must equal the validated daemon pid — a Running
        // label whose job pid is absent/unparseable or points elsewhere is unconfirmed, not success.
        // Managers that cannot supply a job pid (systemd, Windows) keep the coarser check; their
        // already-enabled arm is the documented verified:false one.
        if (state == ServiceState.Running && daemonPid is not null
            && (!jobPidAvailable || jobPid == daemonPid))
            return new EnsureDecision(EnsureAction.AlreadyEnabled);

        // Running without a validated pid, or on launchd without the job owning it, is unconfirmed.
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
/// Pure: the wire fields for an ensure failure — <c>recovery</c> and <c>reason</c>. Kept separate
/// from I/O so the JSON contract is testable without a real service manager. A gate refusal maps
/// its <c>start_gate_reason=</c> token through <see cref="ReasonRouting"/> (takeover/reinstall/
/// attention); drift is the gate's TOCTOU re-check refusing, surfaced as the attention row with
/// its token, never auto-retried; a gated-install viability abort carries the engine's coded
/// <c>viability_reason=</c> token (package_inconsistent → reinstall) when one was emitted; an
/// attributed readiness-timeout boot refusal carries the marker's coded token through
/// <see cref="ReasonRouting.ForBootRefusal"/> (takeover/storage/attention) instead of collapsing
/// to the generic verify token. Every other exit on a verified run carries its <c>verify_*</c>
/// token; a plain (non-launchd) failure carries a plain token, never a <c>verify_*</c> one — the
/// verify prefix must not claim a transaction that never ran.
/// </summary>
internal static class EnsureFailureMap {
    public static (string? Recovery, string? Reason) Map(
            int exit, StartGateReason? gateReason, bool verified,
            string? viabilityReason = null, string? bootRefusalToken = null) {
        if (exit == VerifyExit.StartGate) {
            var reason = gateReason is { } r ? ServiceVerify.GateReasonToken(r) : null;
            var surface = reason is not null
                ? ReasonRouting.ForStartGate(reason)
                : RecoverySurface.Attention;
            return (RecoverySurfaceTokens.Token(surface), reason);
        }

        if (exit == VerifyExit.StartGateDrift)
            // Drift is the gate's TOCTOU re-check refusing — a gate failure, so it surfaces the
            // attention row, never auto-retried (same rule as the app's own table); the reason token
            // keeps the JSON and the human line from reading empty.
            return (RecoverySurfaceTokens.Token(RecoverySurface.Attention), VerifyExitToken(exit));

        // A gated-install viability abort with a coded reason: the engine's own package_inconsistent
        // evidence routes to reinstall (the same token the start gate's table maps), never the
        // generic verify_viability token that says nothing about what to do next.
        if (exit == VerifyExit.Viability && viabilityReason is not null)
            return (RecoverySurfaceTokens.Token(ReasonRouting.ForDaemonStart(viabilityReason)), viabilityReason);

        // An attributed readiness-timeout boot refusal carries the marker's coded token — the flow
        // can route consent_seed_unwritable to storage or server_expectation_mismatch to takeover
        // instead of seeing only verify_readiness_timeout. Unattributed timeouts fall through.
        if (exit == VerifyExit.ReadinessTimeout && bootRefusalToken is not null)
            return (RecoverySurfaceTokens.Token(ReasonRouting.ForBootRefusal(bootRefusalToken)), bootRefusalToken);

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
