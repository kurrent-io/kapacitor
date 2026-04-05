using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace kapacitor.Commands;

static partial class ReviewCommand {
    public static async Task<int> HandleReview(string baseUrl, string prIdentifier) {
        // Parse PR identifier
        if (!TryParsePr(prIdentifier, out var owner, out var repo, out var prNumber)) {
            Console.Error.WriteLine($"Could not parse PR identifier: {prIdentifier}");
            Console.Error.WriteLine("Expected formats:");
            Console.Error.WriteLine("  URL:       https://github.com/owner/repo/pull/123");
            Console.Error.WriteLine("  Shorthand: owner/repo#123");

            return 1;
        }

        Console.Error.WriteLine($"Reviewing PR #{prNumber} in {owner}/{repo}...");

        // Verify that review context exists on the server
        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);

        try {
            var response = await client.GetAsync($"{baseUrl}/api/review/{owner}/{repo}/pulls/{prNumber}");

            if (!response.IsSuccessStatusCode) {
                var status = (int)response.StatusCode;

                if (status == 404) {
                    Console.Error.WriteLine($"No review context found for {owner}/{repo}#{prNumber}.");
                    Console.Error.WriteLine("Make sure the PR has sessions tracked in Capacitor.");
                } else if (await HttpClientExtensions.HandleUnauthorizedAsync(response)) {
                    // 401 message already printed
                } else {
                    Console.Error.WriteLine($"Server returned HTTP {status} when checking review context.");
                }

                return 1;
            }
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        // Build MCP config using JsonNode to avoid AOT/trimming warnings
        var kapacitorPath = Environment.ProcessPath ?? "kapacitor";

        var mcpConfig = new JsonObject {
            ["mcpServers"] = new JsonObject {
                ["kapacitor-review"] = new JsonObject {
                    ["command"] = kapacitorPath,
                    ["args"] = new JsonArray(
                        "mcp", "review",
                        "--owner", owner,
                        "--repo", repo,
                        "--pr", prNumber.ToString()
                    ),
                    ["env"] = new JsonObject {
                        ["KAPACITOR_URL"] = baseUrl
                    }
                }
            }
        };

        var configPath = Path.Combine(Path.GetTempPath(), $"kapacitor-review-{Guid.NewGuid():N}.json");

        try {
            var json = mcpConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(configPath, json);

            Console.Error.WriteLine($"Launching claude with review MCP server...");

            var psi = new ProcessStartInfo {
                FileName        = "claude",
                UseShellExecute = true
            };

            psi.ArgumentList.Add("--mcp-config");
            psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add("--system-prompt");
            psi.ArgumentList.Add(
                $"You are reviewing PR #{prNumber} in {owner}/{repo}. " +
                "Use the kapacitor-review MCP tools to understand the context behind changes. " +
                "Start by calling get_pr_summary to get an overview, then dive into specific files or sessions as needed. " +
                "Focus on understanding the intent behind changes, not just the code diff."
            );

            var process = Process.Start(psi);

            if (process is null) {
                Console.Error.WriteLine("Failed to start claude. Make sure it is installed and on your PATH.");

                return 1;
            }

            await process.WaitForExitAsync();

            return process.ExitCode;
        } finally {
            try {
                File.Delete(configPath);
            } catch {
                // Best effort cleanup
            }
        }
    }

    static bool TryParsePr(string input, out string owner, out string repo, out int prNumber) {
        owner    = "";
        repo     = "";
        prNumber = 0;

        // Try URL format: https://github.com/owner/repo/pull/123
        var urlMatch = UrlPattern().Match(input);

        if (urlMatch.Success) {
            owner    = urlMatch.Groups[1].Value;
            repo     = urlMatch.Groups[2].Value;
            prNumber = int.Parse(urlMatch.Groups[3].Value);

            return true;
        }

        // Try shorthand format: owner/repo#123
        var shortMatch = ShorthandPattern().Match(input);

        if (shortMatch.Success) {
            owner    = shortMatch.Groups[1].Value;
            repo     = shortMatch.Groups[2].Value;
            prNumber = int.Parse(shortMatch.Groups[3].Value);

            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^https?://github\.com/([^/]+)/([^/]+)/pull/(\d+)(?:/.*)?$")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^([^/]+)/([^#]+)#(\d+)$")]
    private static partial Regex ShorthandPattern();
}
