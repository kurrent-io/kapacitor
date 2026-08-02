namespace Capacitor.Cli.SessionStartMemory;

internal static class SessionStartMemoryLifecyclePolicy {
    public static SessionMemoryLifecycleDecision Decide(SessionMemoryLifecycle lifecycle) {
        if (!lifecycle.ClassificationAuthoritative ||
            SessionStartMemoryIdentity.NormalizeSessionId(lifecycle.Harness, lifecycle.SessionId) is null ||
            lifecycle.Reason == SessionLifecycleReason.Unknown)
            return SessionMemoryLifecycleDecision.RetryLaterNoCommit;
        if (!lifecycle.IsTopLevel || lifecycle.Reason == SessionLifecycleReason.Compact)
            return SessionMemoryLifecycleDecision.IneligibleNoCommit;
        // A context reset with no discriminator cannot be told apart from the session start that already
        // injected, so honouring it would re-inject on every subsequent lifecycle event rather than once
        // per clear. Suppressed instead — the same outcome as today, never worse.
        if (lifecycle.Reason == SessionLifecycleReason.Clear && lifecycle.LifecycleInstanceId is null)
            return SessionMemoryLifecycleDecision.IneligibleNoCommit;
        return lifecycle.CallbackMayRepeat
            ? SessionMemoryLifecycleDecision.EligibleWithLease
            : SessionMemoryLifecycleDecision.EligibleOneShot;
    }
}
