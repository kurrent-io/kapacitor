using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
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
            HarnessRegistry harnesses, HarnessOfferStore store, bool optedOut, DateTimeOffset now) =>
        FormatFragment(ClaimAndStamp(harnesses, store, optedOut, now));

    /// <summary>Surface 2: the interactive stderr notice, one line per nudgeable vendor. Null when
    /// opted out, throttled, or nothing is nudgeable.</summary>
    public static string? ResolveNotice(
            HarnessRegistry harnesses, HarnessOfferStore store, bool optedOut, DateTimeOffset now) =>
        FormatNotice(ClaimAndStamp(harnesses, store, optedOut, now));

    /// <summary>Hook-site convenience: resolve the SessionStart fragment from the current process
    /// environment and the default on-disk ledger/throttle. <paramref name="optedOut"/> is the
    /// profile's <c>DisableHarnessNudge</c>.</summary>
    public static string? ResolveFragmentForHook(bool optedOut, ConfigRoot config, UserHome home) =>
        ResolveFragment(HarnessRegistry.FromEnvironment(home), new HarnessOfferStore(config),
                        optedOut, DateTimeOffset.UtcNow);

    /// <summary>Joins an existing SessionStart nudge with the harness nudge (either may be null)
    /// into one additional-context blob, blank-line separated — so a delivery helper that carries a
    /// single nudge slot can carry both.</summary>
    public static string? Combine(string? existing, string? harnessNudge) {
        if (string.IsNullOrWhiteSpace(harnessNudge)) return existing;
        if (string.IsNullOrWhiteSpace(existing)) return harnessNudge;
        return existing + "\n\n" + harnessNudge;
    }

    static IReadOnlyList<IHarness> ClaimAndStamp(
            HarnessRegistry harnesses, HarnessOfferStore store, bool optedOut, DateTimeOffset now) {
        try {
            if (optedOut) return [];
            if (!store.TryClaimCheck(CheckThrottle)) return [];

            var nudgeable = HarnessNudge.Nudgeable(harnesses, store.Load(), now);
            if (nudgeable.Count == 0) return [];

            // Zero-wait lock on the hook path: never spend a SessionStart hook's exit budget waiting
            // on the ledger mutex — skip stamping on contention (re-nudges next window).
            store.StampOffered(nudgeable.Select(h => h.Id), now, TimeSpan.Zero);
            return nudgeable;
        } catch {
            return []; // a nudge must never break a hook or a command
        }
    }

    static string InstallCommand(IHarness h) =>
        h.Id.PluginInstallFlag is { } flag ? $"kcap plugin install {flag}" : "kcap plugin install";

    static string? FormatFragment(IReadOnlyList<IHarness> harnesses) {
        if (harnesses.Count == 0) return null;

        var lines = new List<string> {
            "One or more coding agents are installed that Kurrent Capacitor is not set up for, so " +
            "sessions in them are not being recorded:"
        };
        foreach (var h in harnesses)
            lines.Add($"- {h.Label} — offer to run `{InstallCommand(h)}` to wire it in (hooks, skills, MCP).");

        var dismiss = harnesses.Select(h => h.Id).DismissCommand();
        lines.Add($"If the user declines, run `{dismiss}` so they are not asked again.");

        return string.Join("\n", lines);
    }

    static string? FormatNotice(IReadOnlyList<IHarness> harnesses) {
        if (harnesses.Count == 0) return null;

        return string.Join("\n", harnesses.Select(h =>
            $"kcap: {h.Label} detected but not set up for recording — run `{InstallCommand(h)}` " +
            $"(or `{new[] { h.Id }.DismissCommand()}` to stop asking)."));
    }
}
