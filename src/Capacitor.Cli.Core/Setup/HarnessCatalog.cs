using Capacitor.Cli.Core.Harness;
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

public static class HarnessCatalog {
    /// <summary>
    /// Every supported harness, in setup/display order (Claude first). The <c>IsWired</c> delegate
    /// calls the vendor's own installer — the wired-check the <c>kcap status</c> Hooks line uses, so
    /// the passive status line and the active nudge can never disagree about a vendor. Both read the
    /// same <see cref="HarnessPaths"/> snapshot detection read, so wiring and detection cannot
    /// resolve a vendor's root differently.
    /// </summary>
    public static readonly IReadOnlyList<KnownHarness> All = [
        new("claude", "Claude Code", null, r => r.Claude,
            p => ClaudePluginInstaller.IsPluginEnabled(p.Claude.UserSettings)),
        new("codex", "Codex", "--codex", r => r.Codex,
            p => CodexHooksInstaller.ReferencesKcapHook(p.Codex.UserHooksJson)),
        new("cursor", "Cursor", "--cursor", r => r.Cursor,
            p => CursorHooksInstaller.IsInstalled(p.Cursor.UserHooksJson)),
        new("copilot", "Copilot", "--copilot", r => r.Copilot,
            p => CopilotHooksInstaller.IsInstalled(p.Copilot.KcapHooksJson)),
        new("gemini", "Gemini", "--gemini", r => r.Gemini,
            p => GeminiHooksInstaller.IsInstalled(p.Gemini.SettingsJson)),
        new("kiro", "Kiro", "--kiro", r => r.Kiro,
            p => KiroHooksInstaller.IsInstalled(p.Kiro.KcapAgentJson)),
        new("pi", "Pi", "--pi", r => r.Pi,
            p => PiExtensionInstaller.IsInstalled(p.Pi.KcapExtension)),
        new("opencode", "OpenCode", "--opencode", r => r.OpenCode,
            p => OpenCodeExtensionInstaller.IsInstalled(p.OpenCode.KcapPlugin)),
        new("antigravity", "Antigravity", "--antigravity", r => r.Antigravity,
            p => AntigravityHooksInstaller.IsInstalled(p.Antigravity.GlobalHooksJson)),
    ];

    public static KnownHarness? ById(string vendorId) =>
        All.FirstOrDefault(h => string.Equals(h.VendorId, vendorId, StringComparison.Ordinal));
}
