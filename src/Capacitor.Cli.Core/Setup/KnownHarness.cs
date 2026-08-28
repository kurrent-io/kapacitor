using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// One supported coding-agent harness: its bare vendor id (<c>"antigravity"</c>), its display
/// label, its <c>kcap plugin install</c> flag (<c>"--" + VendorId</c>, or <c>null</c> for flagless
/// Claude), its <see cref="AgentDetectionResult"/> selector, and its wired-check (delegates to the
/// vendor's own installer under <c>Harness/&lt;Vendor&gt;/</c>). This is the single Core registration
/// site: adding a harness is one entry here, not edits scattered across shared code.
/// </summary>
public sealed record KnownHarness(
    string VendorId, string Label, string? InstallFlag,
    Func<AgentDetectionResult, DetectedAgent> Select,
    Func<HarnessPaths, bool> IsWired);
