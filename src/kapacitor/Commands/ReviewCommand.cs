using System.Diagnostics;
using System.Text.RegularExpressions;

namespace kapacitor.Commands;

static partial class ReviewCommand {
    public static async Task<int> HandleReview(string baseUrl, string prIdentifier) {
        // Parse PR identifier
        if (!TryParsePr(prIdentifier, out var owner, out var repo, out var prNumber)) {
            await Console.Error.WriteLineAsync($"Could not parse PR identifier: {prIdentifier}");
            await Console.Error.WriteLineAsync("Expected formats:");
            await Console.Error.WriteLineAsync("  URL:       https://github.com/owner/repo/pull/123");
            await Console.Error.WriteLineAsync("  Shorthand: owner/repo#123");

            return 1;
        }

        await Console.Error.WriteLineAsync($"Reviewing PR #{prNumber} in {owner}/{repo}...");

        // Verify that review context exists on the server
        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);

        try {
            var response = await client.GetAsync($"{baseUrl}/api/review/{owner}/{repo}/pulls/{prNumber}");

            if (!response.IsSuccessStatusCode) {
                var status = (int)response.StatusCode;

                if (status == 404) {
                    await Console.Error.WriteLineAsync($"No review context found for {owner}/{repo}#{prNumber}.");
                    await Console.Error.WriteLineAsync("Make sure the PR has sessions tracked in Capacitor.");
                } else if (await HttpClientExtensions.HandleUnauthorizedAsync(response)) {
                    // 401 message already printed
                } else {
                    await Console.Error.WriteLineAsync($"Server returned HTTP {status} when checking review context.");
                }

                return 1;
            }
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        var launch = await ReviewLaunchBuilder.BuildAsync(baseUrl, owner, repo, prNumber);

        try {
            await Console.Error.WriteLineAsync("Launching claude with review MCP server...");

            var psi = new ProcessStartInfo {
                FileName        = "claude",
                UseShellExecute = true
            };

            psi.ArgumentList.Add("--mcp-config");
            psi.ArgumentList.Add(launch.McpConfigPath);
            psi.ArgumentList.Add("--system-prompt");
            psi.ArgumentList.Add(launch.SystemPrompt);

            var process = Process.Start(psi);

            if (process is null) {
                await Console.Error.WriteLineAsync("Failed to start claude. Make sure it is installed and on your PATH.");

                return 1;
            }

            await process.WaitForExitAsync();

            return process.ExitCode;
        } finally {
            try {
                File.Delete(launch.McpConfigPath);
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
