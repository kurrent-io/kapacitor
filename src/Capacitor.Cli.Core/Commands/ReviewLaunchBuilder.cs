using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Commands;

/// <summary>
/// Shared helper that builds a PR-review launch for a given vendor: the rendered
/// review system prompt (PR placeholders substituted) plus a vendor-neutral MCP
/// server descriptor pointing at <c>kcap mcp review</c>. For Claude it also writes
/// the temp <c>--mcp-config</c> JSON; Codex injects the same server via <c>-c</c>
/// overrides and needs no file. The kcap CLI path is passed in because inside the
/// daemon the running process is <c>kcap-daemon</c> (no <c>mcp review</c> subcommand).
/// </summary>
public static class ReviewLaunchBuilder {
    public record ReviewMcpServer(string Command, string[] Args, IReadOnlyDictionary<string, string> Env);

    public record ReviewLaunch(string? McpConfigPath, string SystemPrompt, ReviewMcpServer Mcp);

    public static async Task<ReviewLaunch> BuildAsync(
            string vendor, string cliPath, string baseUrl, string owner, string repo, int prNumber) {
        // Render the system prompt first. EmbeddedResources.Load can throw; building
        // the prompt before writing any temp file keeps the file's lifetime fully
        // inside the caller's try/finally so a throw never leaks a path-less file.
        var systemPrompt = EmbeddedResources.Load("prompt-review.txt")
            .Replace("{prNumber}", prNumber.ToString(CultureInfo.InvariantCulture))
            .Replace("{owner}", owner)
            .Replace("{repo}", repo);

        var mcp = new ReviewMcpServer(
            Command: cliPath,
            Args: ["mcp", "review", "--owner", owner, "--repo", repo, "--pr", prNumber.ToString(CultureInfo.InvariantCulture)],
            Env: new Dictionary<string, string> { ["KCAP_URL"] = baseUrl });

        string? configPath = null;

        if (vendor == "claude") {
            configPath = await WriteClaudeMcpConfigAsync(mcp);
        }

        return new ReviewLaunch(configPath, systemPrompt, mcp);
    }

    static async Task<string> WriteClaudeMcpConfigAsync(ReviewMcpServer mcp) {
        // Use the implicit string -> JsonValue conversion (cast to JsonNode?) rather
        // than JsonValue.Create / collection expressions, which lower to generic
        // Add<T> and trip NativeAOT (IL3050). This matches the existing pattern.
        var argsNode = new JsonArray();

        foreach (var a in mcp.Args) {
            argsNode.Add((JsonNode?)a);
        }

        var envNode = new JsonObject();

        foreach (var kv in mcp.Env) {
            envNode[kv.Key] = (JsonNode?)kv.Value;
        }

        var mcpConfig = new JsonObject {
            ["mcpServers"] = new JsonObject {
                ["kcap-review"] = new JsonObject {
                    ["command"] = mcp.Command,
                    ["args"]    = argsNode,
                    ["env"]     = envNode
                }
            }
        };

        var configPath = Path.Combine(Path.GetTempPath(), $"kcap-review-{Guid.NewGuid():N}.json");
        var json       = mcpConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, json);

        return configPath;
    }
}
