using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli;

/// <summary>
/// Durable once-per-session claim for the SessionStart nudges on repeating start callbacks with no
/// vendor counter to key on. False on a repeat, an unusable session id, or an unavailable store:
/// the nudges land in a context channel the harness persists, so suppressing beats re-injecting.
/// </summary>
internal static class NudgeLease {
    /// <summary>Store construction happens inside the failure boundary — an unusable store root
    /// must suppress the nudge, never abort the hook.</summary>
    public static async Task<bool> TryClaimAsync(
            ConfigRoot config, TimeProvider time, HarnessId harness, string sessionId, TimeSpan budget) {
        try {
            return await TryClaimAsync(SessionStartMemoryLeaseStore.Create(config, time), harness, sessionId, budget);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return false;
        }
    }

    public static async Task<bool> TryClaimAsync(
            SessionStartMemoryLeaseStore store, HarnessId harness, string sessionId, TimeSpan budget) {
        try {
            var key = SessionStartMemoryIdentity.CreateNudgeKey(harness, sessionId);
            var started = Stopwatch.GetTimestamp();
            var lease = await store.TryBeginAsync(key, budget);
            if (lease is null) return false;
            var remaining = budget - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return false;
            // An uncompleted claim (a crash or exhausted budget between the two calls) expires with
            // the lease, so a later prompt still emits — delayed, never duplicated.
            return await store.CompleteAsync(lease, SessionStartMemoryDisposition.CompleteWithoutContext, remaining);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return false;
        }
    }
}
