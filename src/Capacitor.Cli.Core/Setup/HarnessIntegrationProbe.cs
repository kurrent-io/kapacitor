namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// Generic dispatch for the "is kcap wired into vendor X?" question — the second half of the nudge
/// predicate (<see cref="HarnessNudge"/>) and the source of truth behind the <c>kcap status</c>
/// Hooks line. The per-vendor wiring lives in each vendor's own installer under
/// <c>Harness/&lt;Vendor&gt;/</c>, wired into the single <see cref="HarnessCatalog"/> registration
/// site; this only routes a vendor id to that entry. Lives in Core so both the CLI and the daemon
/// can call it.
/// </summary>
public static class HarnessIntegrationProbe {
    public static bool IsWired(string vendorId, AgentDetectionInputs inputs) =>
        HarnessCatalog.ById(vendorId)?.IsWired(inputs) ?? false;
}
