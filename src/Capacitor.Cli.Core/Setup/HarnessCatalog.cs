using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;

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
    Func<AgentDetectionInputs, bool> IsWired);

public static class HarnessCatalog {
    /// <summary>
    /// Every supported harness, in setup/display order (Claude first). The <c>IsWired</c> delegate
    /// calls the vendor's own installer — the wired-check the <c>kcap status</c> Hooks line uses, so
    /// the passive status line and the active nudge can never disagree about a vendor.
    ///
    /// <para><b>Snapshot purity.</b> Every override the injected <see cref="AgentDetectionInputs"/>
    /// carries is resolved through the vendor's <c>*Pure</c> path helper, so a null override means
    /// "unset → home default" (never a re-read of ambient env) and wiring consumes the SAME snapshot
    /// detection does. Claude and Codex are the sole exception: their config-dir overrides
    /// (<c>CLAUDE_CONFIG_DIR</c>/<c>CODEX_HOME</c>) are not part of the snapshot at all — detection
    /// for those two is PATH-only — so the wired-probe reads them from ambient env. In production the
    /// snapshot is <see cref="AgentDetection.FromEnvironment"/>, so ambient and snapshot coincide and
    /// nothing diverges; only a synthetic snapshot that disagrees with current ambient state could,
    /// and that is accepted.</para>
    /// </summary>
    public static readonly IReadOnlyList<KnownHarness> All = [
        new("claude", "Claude Code", null, r => r.Claude,
            i => ClaudePluginInstaller.IsPluginEnabled(Path.Combine(ClaudePaths.Home(i.Home), "settings.json"))),
        new("codex", "Codex", "--codex", r => r.Codex,
            i => CodexHooksInstaller.ReferencesKcapHook(Path.Combine(CodexPaths.Home(i.Home), "hooks.json"))),
        new("cursor", "Cursor", "--cursor", r => r.Cursor,
            i => CursorHooksInstaller.IsInstalled(CursorPaths.UserHooksJson(i.Home))),
        new("copilot", "Copilot", "--copilot", r => r.Copilot,
            i => CopilotHooksInstaller.IsInstalled(CopilotPaths.KcapHooksJsonPure(i.Home, i.CopilotHome))),
        new("gemini", "Gemini", "--gemini", r => r.Gemini,
            i => GeminiHooksInstaller.IsInstalled(GeminiPaths.SettingsJsonPure(i.Home, i.GeminiCliHome))),
        new("kiro", "Kiro", "--kiro", r => r.Kiro,
            i => KiroHooksInstaller.IsInstalled(KiroPaths.KcapAgentJsonPure(i.Home, i.KiroHome))),
        new("pi", "Pi", "--pi", r => r.Pi,
            i => PiExtensionInstaller.IsInstalled(PiPaths.KcapExtensionPure(i.Home, i.PiAgentDir))),
        new("opencode", "OpenCode", "--opencode", r => r.OpenCode,
            i => OpenCodeExtensionInstaller.IsInstalled(OpenCodePaths.KcapPluginPure(i.Home, i.OpenCodeConfigDir, i.XdgConfigHome))),
        new("antigravity", "Antigravity", "--antigravity", r => r.Antigravity,
            i => AntigravityHooksInstaller.IsInstalled(AntigravityPaths.GlobalHooksJsonPure(i.Home, i.GeminiCliHome))),
    ];

    public static KnownHarness? ById(string vendorId) =>
        All.FirstOrDefault(h => string.Equals(h.VendorId, vendorId, StringComparison.Ordinal));
}
