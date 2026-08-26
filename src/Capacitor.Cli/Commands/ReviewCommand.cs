using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Commands;

class ReviewCommand(ConfigRoot config, ProfileContext profiles) {
    public async Task<int> HandleReview(string prIdentifier) {
        var baseUrl = profiles.Resolution.ServerUrl!;

        // Parse PR identifier
        if (!PrRefParser.TryParse(prIdentifier, out var owner, out var repo, out var prNumber)) {
            await Console.Error.WriteLineAsync($"Could not parse PR identifier: {prIdentifier}");
            await Console.Error.WriteLineAsync("Expected formats:");
            await Console.Error.WriteLineAsync("  GitHub URL:  https://github.com/owner/repo/pull/123");
            await Console.Error.WriteLineAsync("  GitLab URL:  https://gitlab.com/owner/repo/-/merge_requests/123");
            await Console.Error.WriteLineAsync("  Shorthand:   owner/repo#123");

            return 1;
        }

        await Console.Error.WriteLineAsync($"Reviewing PR #{prNumber} in {owner}/{repo}...");

        // Verify that review context exists on the server
        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(config, profiles, baseUrl);

        try {
            var response = await client.GetAsync(
                $"{baseUrl}/api/review/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{prNumber}");

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

        var launch = await ReviewLaunchBuilder.BuildAsync(
            "claude", Environment.ProcessPath ?? "kcap", baseUrl, owner, repo, prNumber);

        try {
            await Console.Error.WriteLineAsync("Launching claude with review MCP server...");

            var psi = new ProcessStartInfo {
                FileName        = "claude",
                UseShellExecute = true
            };

            psi.ArgumentList.Add("--mcp-config");
            psi.ArgumentList.Add(launch.McpConfigPath!);
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
                File.Delete(launch.McpConfigPath!);
            } catch {
                // Best effort cleanup
            }
        }
    }

}
