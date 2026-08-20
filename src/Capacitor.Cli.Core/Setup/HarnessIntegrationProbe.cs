using System.Text.Json.Nodes;
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
/// Answers "is kcap's integration wired into vendor X on this machine?" — the second half of the
/// nudge predicate (<see cref="HarnessNudge"/>) and the single source of truth behind the
/// <c>kcap status</c> Hooks line. Lives in Core so both the CLI and the daemon can call it. Paths
/// are resolved from an injected <see cref="AgentDetectionInputs"/> (the same snapshot detection
/// uses), so tests never touch process-wide state.
///
/// <para>The per-vendor checks are exactly what the <c>kcap status</c> line has always shown — the
/// seven marker/config installers, Claude's enabled-plugin flag, and Codex's hooks-reference — so
/// the passive status line and the active nudge can never disagree about a given vendor.</para>
/// </summary>
public static class HarnessIntegrationProbe {
    public static bool IsWired(string vendorId, AgentDetectionInputs inputs) {
        var home = inputs.Home;
        return vendorId switch {
            "claude"      => ClaudePluginEnabled(Path.Combine(ClaudePaths.Home(home), "settings.json")),
            "codex"       => CodexHooksReferenced(Path.Combine(CodexPaths.Home(home), "hooks.json")),
            "cursor"      => CursorHooksInstaller.IsInstalled(CursorPaths.UserHooksJson(home)),
            "copilot"     => CopilotHooksInstaller.IsInstalled(CopilotPaths.KcapHooksJson(home, inputs.CopilotHome)),
            "gemini"      => GeminiHooksInstaller.IsInstalled(GeminiPaths.SettingsJson(home, inputs.GeminiCliHome)),
            "kiro"        => KiroHooksInstaller.IsInstalled(KiroPaths.KcapAgentJson(home)),
            "pi"          => PiExtensionInstaller.IsInstalled(PiPaths.KcapExtension(home)),
            "opencode"    => OpenCodeExtensionInstaller.IsInstalled(OpenCodePaths.KcapPlugin(home, inputs.XdgConfigHome)),
            "antigravity" => AntigravityHooksInstaller.IsInstalled(AntigravityPaths.GlobalHooksJson(home, inputs.GeminiCliHome)),
            _             => false,
        };
    }

    /// <summary>True iff <paramref name="settingsPath"/> has <c>enabledPlugins["kcap@kcap"] == true</c>.
    /// The Claude wired-check the status line has always used.</summary>
    public static bool ClaudePluginEnabled(string settingsPath) {
        try {
            if (!File.Exists(settingsPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return false;
            if (root["enabledPlugins"] is not JsonObject enabled) return false;

            return enabled["kcap@kcap"]?.GetValue<bool>() == true;
        } catch {
            return false;
        }
    }

    /// <summary>True iff <paramref name="hooksPath"/> has any hook entry referencing the
    /// <c>kcap codex-hook</c> command. The Codex wired-check the status line has always used.</summary>
    public static bool CodexHooksReferenced(string hooksPath) {
        try {
            if (!File.Exists(hooksPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, value) in hooks) {
                if (value is JsonArray entries && entries.Any(CodexHooksParser.EntryReferencesCapacitorCodexHook))
                    return true;
            }

            return false;
        } catch {
            return false;
        }
    }
}
