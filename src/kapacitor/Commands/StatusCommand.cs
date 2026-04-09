using kapacitor.Auth;

namespace kapacitor.Commands;

public static class StatusCommand {
    public static async Task<int> HandleAsync(string? baseUrl) {
        // Server
        Console.Write("  Server:  ");

        if (baseUrl is null) {
            await Console.Out.WriteLineAsync("not configured");
        } else {
            Console.Write($"{baseUrl} ");

            try {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                var resp = await http.GetAsync($"{baseUrl}/auth/config");
                await Console.Out.WriteLineAsync(resp.IsSuccessStatusCode ? "✓ reachable" : $"✗ HTTP {(int)resp.StatusCode}");
            } catch {
                await Console.Out.WriteLineAsync("✗ unreachable");
            }
        }

        // Auth
        Console.Write("  Auth:    ");
        var tokens = await TokenStore.GetValidTokensAsync();

        if (tokens is not null) {
            var remaining = tokens.ExpiresAt - DateTimeOffset.UtcNow;

            var expiryText = remaining.TotalHours > 1
                ? $"expires in {remaining.TotalHours:F0}h"
                : $"expires in {remaining.TotalMinutes:F0}m";
            await Console.Out.WriteLineAsync($"{tokens.GitHubUsername} ({tokens.Provider}) ✓ token valid ({expiryText})");
        } else {
            var rawTokens = await TokenStore.LoadAsync();

            await Console.Out.WriteLineAsync(rawTokens is not null ? $"{rawTokens.GitHubUsername} ({rawTokens.Provider}) ✗ token expired" : "not authenticated (run: kapacitor login)");
        }

        // Agent
        Console.Write("  Agent:   ");

        var pidPath = PathHelpers.ConfigPath("agent.pid");

        if (File.Exists(pidPath)) {
            var pidStr = (await File.ReadAllTextAsync(pidPath)).Trim();

            if (int.TryParse(pidStr, out var pid)) {
                try {
                    System.Diagnostics.Process.GetProcessById(pid);
                    await Console.Out.WriteLineAsync($"running (PID {pid})");
                } catch (ArgumentException) {
                    await Console.Out.WriteLineAsync("not running (stale PID file)");
                }
            } else {
                await Console.Out.WriteLineAsync("unknown (invalid PID file)");
            }
        } else {
            await Console.Out.WriteLineAsync("not running");
        }

        return 0;
    }
}
