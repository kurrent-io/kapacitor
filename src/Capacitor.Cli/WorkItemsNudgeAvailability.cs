using System.Text;
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
            // ReadMcpServerCommands requires each returned table to carry a `command` string, so a
            // malformed/command-less [mcp_servers.kcap-workitems] table does NOT count (fail-closed).
            // Codex has no per-server enable flag, so a valid command table is a live registration.
            return CodexConfigToml.ReadMcpServerCommands(codexConfigPath)
                .Any(s => string.Equals(s.Name, ServerName, StringComparison.OrdinalIgnoreCase));
        } catch {
            return false;
        }
    }

    static bool PiHasWorkItems(string? home) {
        try {
            var path = PiPaths.KcapMcpExtension(home);
            if (!File.Exists(path)) return false;
            // Strip JS comments FIRST so a commented-out `KCAP_MCP_SERVERS = [...]` before the real
            // declaration can't be matched, then find the real `KCAP_MCP_SERVERS = [ … ]` array and
            // require "workitems" as an exact array ELEMENT — not a substring, so a token inside an
            // unrelated string doesn't count either.
            var content = StripJsComments(File.ReadAllText(path));
            for (var k = content.IndexOf("KCAP_MCP_SERVERS", StringComparison.Ordinal);
                 k >= 0;
                 k = content.IndexOf("KCAP_MCP_SERVERS", k + 1, StringComparison.Ordinal)) {
                var eq = content.IndexOf('=', k);
                if (eq < 0) continue;
                var open = content.IndexOf('[', eq);
                if (open < 0) continue;
                var close = content.IndexOf(']', open);
                if (close < 0) return false;
                var elements = content.Substring(open + 1, close - open - 1)
                    .Split(',')
                    .Select(e => e.Trim());
                return elements.Any(e => e is "\"workitems\"" or "'workitems'");
            }
            return false;
        } catch {
            return false;
        }
    }

    /// <summary>Removes JS line (<c>//…</c>) and block (<c>/* … */</c>) comments so a commented-out
    /// server list can't be mistaken for the real declaration. Not string-literal-aware, which is
    /// safe here: the generated extension carries no <c>//</c> or <c>/*</c> inside the single-line
    /// string literals near the declaration.</summary>
    static string StripJsComments(string s) {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++) {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/') {
                i += 2;
                while (i < s.Length && s[i] != '\n') i++;
                if (i < s.Length) sb.Append('\n'); // keep the newline
            } else if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*') {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                i++; // land on the '/' of '*/'; the loop's i++ steps past it
            } else {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    static bool JsonBlockHasServer(string path, string blockKey) {
        try {
            if (!File.Exists(path)) return false;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return false;
            if (root[blockKey] is not JsonObject block) return false;

            foreach (var (name, entry) in block) {
                if (!string.Equals(name, ServerName, StringComparison.OrdinalIgnoreCase)) continue;
                // A materialized MCP entry is always an object (command/args or type/enabled). Anything
                // else — null, a string, an array — is malformed and fails closed.
                if (entry is not JsonObject o) return false;
                // Honor an explicit enable flag STRICTLY: only a Boolean `true` counts. A `false`, a
                // non-Boolean (e.g. "false"), or any other shape suppresses. Absent flag ⇒ enabled
                // (the JSON harnesses other than OpenCode carry no enable flag).
                if (o.TryGetPropertyValue("enabled", out var enNode))
                    return enNode is JsonValue enVal && enVal.TryGetValue<bool>(out var enabled) && enabled;
                return true;
            }
            return false;
        } catch {
            return false;
        }
    }
}
