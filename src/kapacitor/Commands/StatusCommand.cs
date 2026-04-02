using kapacitor.Auth;

namespace kapacitor.Commands;

public static class StatusCommand {
    public static async Task<int> HandleAsync(string? baseUrl) {
        // Server
        Console.Write("  Server:  ");

        if (baseUrl is null) {
            Console.WriteLine("not configured");
        } else {
            Console.Write($"{baseUrl} ");

            try {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                var resp = await http.GetAsync($"{baseUrl}/auth/config");
                Console.WriteLine(resp.IsSuccessStatusCode ? "✓ reachable" : $"✗ HTTP {(int)resp.StatusCode}");
            } catch {
                Console.WriteLine("✗ unreachable");
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
            Console.WriteLine($"{tokens.GitHubUsername} ({tokens.Provider}) ✓ token valid ({expiryText})");
        } else {
            var rawTokens = TokenStore.Load();

            Console.WriteLine(rawTokens is not null ? $"{rawTokens.GitHubUsername} ({rawTokens.Provider}) ✗ token expired" : "not authenticated (run: kapacitor login)");
        }

        // Agent
        Console.Write("  Agent:   ");

        var pidPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "kapacitor",
            "agent.pid"
        );

        if (File.Exists(pidPath)) {
            var pidStr = (await File.ReadAllTextAsync(pidPath)).Trim();

            if (int.TryParse(pidStr, out var pid)) {
                try {
                    System.Diagnostics.Process.GetProcessById(pid);
                    Console.WriteLine($"running (PID {pid})");
                } catch (ArgumentException) {
                    Console.WriteLine("not running (stale PID file)");
                }
            } else {
                Console.WriteLine("unknown (invalid PID file)");
            }
        } else {
            Console.WriteLine("not running");
        }

        return 0;
    }
}
