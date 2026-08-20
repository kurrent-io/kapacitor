using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli;

/// <summary>
/// Builds the "you installed a new harness — set kcap up for it?" nudge, shared by two surfaces:
/// the SessionStart agent-facing fragment (<see cref="ResolveFragment"/>) and the interactive CLI
/// stderr notice (<see cref="ResolveNotice"/>). Both run the same claim-throttle → detect →
/// predicate → stamp core over injected inputs, then format differently.
///
/// <para>The 6-hour evaluation throttle is shared across both surfaces (one on-disk stamp), a
/// deliberate hierarchy: whichever fires first in a window claims it, surface 1 (in-session, the
/// agent can act immediately) is primary and surface 2 the fallback. A given vendor still re-nudges
/// at most once per <see cref="HarnessNudge.ReofferFloor"/> regardless of how often the check runs.</para>
///
/// <para>Failure posture mirrors the other SessionStart emitters: any exception degrades to "no
/// nudge" at the call sites; a nudge must never break a hook.</para>
/// </summary>
static class HarnessNudgeEmitter {
    public static readonly TimeSpan CheckThrottle = TimeSpan.FromHours(6);

    /// <summary>Surface 1: the SessionStart <c>additionalContext</c> fragment telling the agent to
    /// offer setup. Null when opted out, throttled, or nothing is nudgeable.</summary>
    public static string? ResolveFragment(
            AgentDetectionInputs inputs, HarnessOfferStore store, bool optedOut, DateTimeOffset now,
            Func<string, AgentDetectionInputs, bool>? isWired = null,
            Func<AgentDetectionInputs, AgentDetectionResult>? detect = null) =>
        FormatFragment(ClaimAndStamp(inputs, store, optedOut, now, isWired, detect));

    /// <summary>Surface 2: the interactive stderr notice, one line per nudgeable vendor. Null when
    /// opted out, throttled, or nothing is nudgeable.</summary>
    public static string? ResolveNotice(
            AgentDetectionInputs inputs, HarnessOfferStore store, bool optedOut, DateTimeOffset now,
            Func<string, AgentDetectionInputs, bool>? isWired = null,
            Func<AgentDetectionInputs, AgentDetectionResult>? detect = null) =>
        FormatNotice(ClaimAndStamp(inputs, store, optedOut, now, isWired, detect));

    /// <summary>Hook-site convenience: resolve the SessionStart fragment from the current process
    /// environment and the default on-disk ledger/throttle. <paramref name="optedOut"/> is the
    /// profile's <c>DisableHarnessNudge</c>.</summary>
    public static string? ResolveFragmentForHook(bool optedOut) =>
        ResolveFragment(AgentDetection.FromEnvironment(), HarnessOfferStore.Default(), optedOut, DateTimeOffset.UtcNow);

    /// <summary>Joins an existing SessionStart nudge with the harness nudge (either may be null)
    /// into one additional-context blob, blank-line separated — so a delivery helper that carries a
    /// single nudge slot can carry both.</summary>
    public static string? Combine(string? existing, string? harnessNudge) {
        if (string.IsNullOrWhiteSpace(harnessNudge)) return existing;
        if (string.IsNullOrWhiteSpace(existing)) return harnessNudge;
        return existing + "\n\n" + harnessNudge;
    }

    static IReadOnlyList<KnownHarness> ClaimAndStamp(
            AgentDetectionInputs inputs, HarnessOfferStore store, bool optedOut, DateTimeOffset now,
            Func<string, AgentDetectionInputs, bool>? isWired,
            Func<AgentDetectionInputs, AgentDetectionResult>? detect) {
        try {
            if (optedOut) return [];
            if (!store.TryClaimCheck(CheckThrottle)) return [];

            var detected  = (detect ?? AgentDetection.Detect)(inputs);
            var ledger    = store.Load();
            var wired     = isWired ?? HarnessIntegrationProbe.IsWired;
            var nudgeable = HarnessNudge.Nudgeable(detected, id => wired(id, inputs), ledger, now);
            if (nudgeable.Count == 0) return [];

            // Zero-wait lock on the hook path: never spend a SessionStart hook's exit budget waiting
            // on the ledger mutex — skip stamping on contention (re-nudges next window).
            store.StampOffered(nudgeable.Select(h => h.VendorId), now, TimeSpan.Zero);
            return nudgeable;
        } catch {
            return []; // a nudge must never break a hook or a command
        }
    }

    static string InstallCommand(KnownHarness h) =>
        h.InstallFlag is null ? "kcap plugin install" : $"kcap plugin install {h.InstallFlag}";

    static string? FormatFragment(IReadOnlyList<KnownHarness> harnesses) {
        if (harnesses.Count == 0) return null;

        var lines = new List<string> {
            "One or more coding agents are installed that Kurrent Capacitor is not set up for, so " +
            "sessions in them are not being recorded:"
        };
        foreach (var h in harnesses)
            lines.Add($"- {h.Label} — offer to run `{InstallCommand(h)}` to wire it in (hooks, skills, MCP).");

        var ids = string.Join(" ", harnesses.Select(h => h.VendorId));
        lines.Add($"If the user declines, run `kcap harness dismiss {ids}` so they are not asked again.");

        return string.Join("\n", lines);
    }

    static string? FormatNotice(IReadOnlyList<KnownHarness> harnesses) {
        if (harnesses.Count == 0) return null;

        return string.Join("\n", harnesses.Select(h =>
            $"kcap: {h.Label} detected but not set up for recording — run `{InstallCommand(h)}` " +
            $"(or `kcap harness dismiss {h.VendorId}` to stop asking)."));
    }
}
