using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Antigravity;
using Capacitor.Cli.Core.Copilot;
using Capacitor.Cli.Core.Cursor;
using Capacitor.Cli.Core.Gemini;
using Capacitor.Cli.Core.Kiro;
using Capacitor.Cli.Core.OpenCode;
using Capacitor.Cli.Core.Pi;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli;

/// <summary>
/// The SessionStart work-items nudge's availability gate: is the <c>kcap-workitems</c> MCP server
/// actually MATERIALIZED in the invoking harness's on-disk config? The nudge tells the agent to use
/// tools that only exist if that server is registered, so a stale install (an upgraded CLI binary
/// whose harness config predates the workitems registration) must NOT be nudged toward a tool it
/// lacks.
///
/// <para>This reads the real config entry — not an ownership marker — so it also catches a
/// manually-removed, disabled, or malformed entry. It is a CONFIG-LEVEL check, deliberately NOT a
/// runtime health probe (we never spawn/handshake the server at hook time). The one residual — a
/// materialized entry whose server nonetheless fails to launch — yields at worst a benign
/// tool-not-found, repaired by <c>kcap setup</c>/<c>kcap doctor</c>.</para>
///
/// <para>Fails CLOSED: any absent / disabled / malformed / unreadable config suppresses the nudge.
/// Claude has always carried <c>kcap-workitems</c> (via the plugin's bundled <c>.mcp.json</c>), so it
/// is always available there.</para>
/// </summary>
static class WorkItemsNudgeAvailability {
    const string ServerName = "kcap-workitems";

    /// <param name="home">Overrides the user home root for the JSON/Pi path helpers (test seam).</param>
    /// <param name="codexConfigPath">Overrides the Codex <c>config.toml</c> path (test seam); null uses the default.</param>
    public static bool IsRegisteredFor(SessionStartHarness harness, string? home = null, string? codexConfigPath = null) {
        try {
            return harness switch {
                // Claude always had kcap-workitems (bundled plugin .mcp.json); if its SessionStart hook
                // is firing, the plugin is installed.
                SessionStartHarness.Claude      => true,
                SessionStartHarness.Codex       => CodexHasWorkItems(codexConfigPath),
                SessionStartHarness.Cursor      => JsonBlockHasServer(CursorPaths.UserMcpJson(home), "mcpServers"),
                SessionStartHarness.Copilot     => JsonBlockHasServer(CopilotPaths.McpConfigJson(home), "mcpServers"),
                SessionStartHarness.Gemini      => JsonBlockHasServer(GeminiPaths.SettingsJson(home), "mcpServers"),
                SessionStartHarness.Kiro        => JsonBlockHasServer(KiroPaths.SettingsMcpJson(home), "mcpServers"),
                // OpenCode's block key is `mcp`, not `mcpServers`.
                SessionStartHarness.OpenCode    => JsonBlockHasServer(OpenCodePaths.McpConfigJson(home), "mcp"),
                SessionStartHarness.Antigravity => JsonBlockHasServer(AntigravityPaths.McpConfigJson(home), "mcpServers"),
                SessionStartHarness.Pi          => PiHasWorkItems(home),
                _ => false
            };
        } catch {
            return false; // fail closed
        }
    }

    static bool CodexHasWorkItems(string? codexConfigPath) {
        try {
            return CodexConfigToml.ReadMcpServerNames(codexConfigPath)
                .Any(n => string.Equals(n, ServerName, StringComparison.OrdinalIgnoreCase));
        } catch {
            return false;
        }
    }

    static bool PiHasWorkItems(string? home) {
        try {
            var path = PiPaths.KcapMcpExtension(home);
            if (!File.Exists(path)) return false;
            // The bridge materializes its server list as bare quoted names in KCAP_MCP_SERVERS; an
            // extension that includes workitems contains the quoted token.
            return File.ReadAllText(path).Contains("\"workitems\"", StringComparison.Ordinal);
        } catch {
            return false;
        }
    }

    static bool JsonBlockHasServer(string path, string blockKey) {
        try {
            if (!File.Exists(path)) return false;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return false;
            if (root[blockKey] is not JsonObject block) return false;

            foreach (var (name, entry) in block) {
                if (!string.Equals(name, ServerName, StringComparison.OrdinalIgnoreCase)) continue;
                // Present. Treat an explicit `"enabled": false` (OpenCode's disable flag) as absent;
                // no other harness shape has a disable flag, so a present entry is enabled by default.
                if (entry is JsonObject o && o["enabled"] is JsonValue en &&
                    en.TryGetValue<bool>(out var enabled) && !enabled)
                    return false;
                return true;
            }
            return false;
        } catch {
            return false;
        }
    }
}
