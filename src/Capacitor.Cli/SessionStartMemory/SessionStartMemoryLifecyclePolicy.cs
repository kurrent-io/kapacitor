namespace Capacitor.Cli.SessionStartMemory;

internal static class SessionStartMemoryLifecyclePolicy {
    public static SessionMemoryLifecycleDecision Decide(SessionMemoryLifecycle lifecycle) {
        if (!lifecycle.ClassificationAuthoritative ||
            SessionStartMemoryIdentity.NormalizeSessionId(lifecycle.Harness, lifecycle.SessionId) is null ||
            lifecycle.Reason == SessionLifecycleReason.Unknown)
            return SessionMemoryLifecycleDecision.RetryLaterNoCommit;
        if (!lifecycle.IsTopLevel || lifecycle.Reason == SessionLifecycleReason.Compact)
            return SessionMemoryLifecycleDecision.IneligibleNoCommit;
        return lifecycle.CallbackMayRepeat
            ? SessionMemoryLifecycleDecision.EligibleWithLease
            : SessionMemoryLifecycleDecision.EligibleOneShot;
    }
}
