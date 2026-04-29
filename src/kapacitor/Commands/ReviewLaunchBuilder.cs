using System.Text.Json;
using System.Text.Json.Nodes;

namespace kapacitor.Commands;

/// <summary>
/// Shared helper that builds the <c>claude</c> invocation for a PR review:
/// a temp MCP config pointing at <c>kapacitor mcp review</c> plus the embedded
/// review system prompt with PR placeholders substituted. Used by both the
/// interactive <c>kapacitor review</c> command and the daemon's hosted-review
/// launch path so the review experience stays identical between them.
/// </summary>
static class ReviewLaunchBuilder {
    public record ReviewLaunch(string McpConfigPath, string SystemPrompt);

    /// <summary>
    /// Writes a temp MCP config and returns the path plus the rendered system
    /// prompt. Caller is responsible for deleting the config file when the
    /// claude process exits.
    /// </summary>
    public static async Task<ReviewLaunch> BuildAsync(string baseUrl, string owner, string repo, int prNumber) {
        // Render the system prompt first. EmbeddedResources.Load can throw
        // (e.g. resource missing under trimming), and if we wrote the temp
        // file before that throw, the caller never sees a path to clean up
        // and the file leaks. Building the prompt up front keeps the temp
        // file's lifetime fully inside the caller's try/finally.
        var systemPrompt = EmbeddedResources.Load("prompt-review.txt")
            .Replace("{prNumber}", prNumber.ToString())
            .Replace("{owner}", owner)
            .Replace("{repo}", repo);

        var kapacitorPath = Environment.ProcessPath ?? "kapacitor";

        var mcpConfig = new JsonObject {
            ["mcpServers"] = new JsonObject {
                ["kapacitor-review"] = new JsonObject {
                    ["command"] = kapacitorPath,
                    ["args"] = new JsonArray(
                        "mcp",
                        "review",
                        "--owner",
                        owner,
                        "--repo",
                        repo,
                        "--pr",
                        prNumber.ToString()
                    ),
                    ["env"] = new JsonObject { ["KAPACITOR_URL"] = baseUrl }
                }
            }
        };

        var configPath = Path.Combine(Path.GetTempPath(), $"kapacitor-review-{Guid.NewGuid():N}.json");
        var json       = mcpConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, json);

        return new ReviewLaunch(configPath, systemPrompt);
    }
}
