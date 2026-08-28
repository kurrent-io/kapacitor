using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// The "is kcap wired into vendor X?" question — the second half of the nudge predicate
/// (<see cref="HarnessNudge"/>) and the source of truth behind the <c>kcap status</c> Hooks line.
/// The per-vendor wiring lives in each vendor's own installer under <c>Harness/&lt;Vendor&gt;/</c>,
/// reached through the single <see cref="HarnessCatalog"/> registration site; this only routes a
/// vendor id to that entry. An extension rather than a member of <see cref="HarnessPaths"/>, so a
/// layout does not have to know about install policy.
/// </summary>
public static class HarnessWiring {
    public static bool IsWired(this HarnessPaths paths, string vendorId) =>
        HarnessCatalog.ById(vendorId)?.IsWired(paths) ?? false;
}
