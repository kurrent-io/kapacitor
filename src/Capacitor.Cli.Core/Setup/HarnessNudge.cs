using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// The shared "which harnesses should we nudge about?" predicate, pure and Core-side so every
/// surface (SessionStart nudge, CLI stderr notice, daemon inventory) computes the same answer. A
/// vendor is nudgeable when it is detected, kcap is not wired into it, it is not declined, and it
/// has not been offered within the re-offer floor.
/// </summary>
public static class HarnessNudge {
    /// <summary>A given vendor re-nudges at most once per this window, even on a fully active
    /// machine — the evaluation throttle (see <see cref="HarnessOfferStore.TryClaimCheck"/>) only
    /// governs how often the check runs, never how often a vendor re-appears.</summary>
    public static readonly TimeSpan ReofferFloor = TimeSpan.FromDays(7);

    /// <param name="harnesses">The harnesses this process sees, in nudge order.</param>
    /// <param name="ledger">The current offer ledger.</param>
    /// <param name="now">Clock (injected for tests).</param>
    public static IReadOnlyList<IHarness> Nudgeable(
            HarnessRegistry harnesses, HarnessOfferLedger ledger, DateTimeOffset now) {
        var result = new List<IHarness>();

        foreach (var harness in harnesses) {
            if (!harnesses.Detected(harness.Id)) continue;
            if (harness.Signals.IsWired) continue;
            var entry = ledger.Entry(harness.Id);
            if (entry is { Declined: true }) continue;
            // A given vendor re-nudges at most once per floor even on a fully active machine.
            if (entry?.LastOffered is { } last && now - last < ReofferFloor) continue;
            result.Add(harness);
        }

        return result;
    }
}
