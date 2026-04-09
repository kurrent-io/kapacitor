using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
// ReSharper disable MethodHasAsyncOverload

namespace kapacitor.Commands;

static class RecapCommand {
    public static async Task<int> HandleRepoRecap(string baseUrl, int limit = 10) {
        var cwd  = Directory.GetCurrentDirectory();
        var repo = await RepositoryDetection.DetectRepositoryAsync(cwd);

        if (repo?.Owner is null || repo.RepoName is null) {
            Console.Error.WriteLine("Not in a git repository with a remote origin.");

            return 1;
        }

        var hash = ComputeRepoHash(repo.Owner, repo.RepoName);

        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();

        HttpResponseMessage resp;

        try {
            resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/repositories/{hash}/recaps?limit={limit}");
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) {
            return 1;
        }

        if (!resp.IsSuccessStatusCode) {
            Console.Error.WriteLine($"HTTP {(int)resp.StatusCode}");

            return 1;
        }

        var json    = await resp.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.ListRepoRecapEntry);

        if (entries is null || entries.Count == 0) {
            await Console.Out.WriteLineAsync($"No session summaries found for {repo.Owner}/{repo.RepoName}.");

            return 0;
        }

        await Console.Out.WriteLineAsync($"# Recent sessions for {repo.Owner}/{repo.RepoName}");
        await Console.Out.WriteLineAsync();

        foreach (var entry in entries) {
            var date  = entry.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var title = entry.Title ?? "(untitled)";
            await Console.Out.WriteLineAsync($"## {title}");
            await Console.Out.WriteLineAsync($"*Session {entry.SessionId} | {date}*");
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync(entry.Summary);
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync($"Full transcript: `kapacitor recap --full {entry.SessionId}`");
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync("---");
            await Console.Out.WriteLineAsync();
        }

        return 0;
    }

    static string ComputeRepoHash(string owner, string repoName) {
        var input = $"{owner}/{repoName}".ToLowerInvariant();
        var hash  = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexStringLower(hash)[..16];
    }

    public static async Task<int> HandleRecap(string baseUrl, string sessionId, bool chain, bool full = false) {
        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        var       query      = chain ? "?chain=true" : "";

        HttpResponseMessage resp;

        try {
            resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/recap{query}");
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) {
            return 1;
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) {
            Console.Error.WriteLine($"Session not found: {sessionId}");

            return 1;
        }

        if (!resp.IsSuccessStatusCode) {
            Console.Error.WriteLine($"HTTP {(int)resp.StatusCode}");

            return 1;
        }

        var json    = await resp.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.ListRecapEntry);

        if (entries is null || entries.Count == 0) {
            await Console.Out.WriteLineAsync("No recap entries found.");

            return 0;
        }

        return full ? PrintFull(entries, chain) : PrintSummary(entries, chain);
    }

    static int PrintSummary(List<RecapEntry> entries, bool chain) {
        var summaries = entries.Where(e => e.Type is "whats_done" or "plan").ToList();

        if (summaries.Count == 0) {
            Console.Out.WriteLine("No summary available yet. Use `kapacitor recap --full` to see the raw transcript.");

            return 0;
        }

        string? currentSessionId = null;

        foreach (var entry in summaries) {
            if (chain && entry.SessionId != currentSessionId) {
                currentSessionId = entry.SessionId;
                Console.Out.WriteLine($"# Session {currentSessionId}");
                Console.Out.WriteLine();
            }

            switch (entry.Type) {
                case "plan":
                    Console.Out.WriteLine("## Plan");
                    Console.Out.WriteLine(entry.Content);
                    Console.Out.WriteLine();

                    break;

                case "whats_done":
                    Console.Out.WriteLine("## Summary");
                    Console.Out.WriteLine(entry.Content);
                    Console.Out.WriteLine();

                    break;
            }
        }

        Console.Error.WriteLine("Use `kapacitor recap --full` for the complete transcript.");

        return 0;
    }

    static int PrintFull(List<RecapEntry> entries, bool chain) {
        string? currentSessionId = null;
        string? currentAgentId   = null;

        foreach (var entry in entries) {
            // Session header in chain mode
            if (chain && entry.SessionId != currentSessionId) {
                currentSessionId = entry.SessionId;
                currentAgentId   = null;
                Console.Out.WriteLine($"# Session {currentSessionId}");
                Console.Out.WriteLine();
            }

            // Agent header when agent changes
            if (entry.AgentId is not null && entry.AgentId != currentAgentId) {
                currentAgentId = entry.AgentId;
                var agentLabel = entry.AgentType is not null ? $"Agent ({entry.AgentType})" : $"Agent {entry.AgentId}";
                Console.Out.WriteLine($"### {agentLabel}");
                Console.Out.WriteLine();
            } else if (entry.AgentId is null && currentAgentId is not null) {
                currentAgentId = null;
            }

            switch (entry.Type) {
                case "plan":
                    Console.Out.WriteLine("## Plan");
                    Console.Out.WriteLine(entry.Content);
                    Console.Out.WriteLine();

                    break;

                case "user_prompt":
                    Console.Out.WriteLine("## User Prompt");
                    Console.Out.WriteLine(entry.Content);
                    Console.Out.WriteLine();

                    break;

                case "assistant_text":
                    Console.Out.WriteLine("## Assistant");
                    Console.Out.WriteLine(entry.Content);
                    Console.Out.WriteLine();

                    break;

                case "write":
                    var writePath = entry.FilePath ?? "unknown";
                    var writeLang = GetLanguageHint(writePath);
                    Console.Out.WriteLine($"## Write {writePath}");
                    Console.Out.WriteLine($"```{writeLang}");
                    Console.Out.WriteLine(entry.Content);
                    Console.Out.WriteLine("```");
                    Console.Out.WriteLine();

                    break;

                case "edit":
                    var editPath = entry.FilePath ?? "unknown";
                    Console.Out.WriteLine($"## Edit {editPath}");
                    Console.Out.WriteLine("```");
                    Console.Out.WriteLine(entry.Content);
                    Console.Out.WriteLine("```");
                    Console.Out.WriteLine();

                    break;
            }
        }

        return 0;
    }

    static string GetLanguageHint(string filePath) {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch {
            ".cs"         => "csharp",
            ".js"         => "javascript",
            ".ts"         => "typescript",
            ".tsx"        => "tsx",
            ".jsx"        => "jsx",
            ".py"         => "python",
            ".rb"         => "ruby",
            ".go"         => "go",
            ".rs"         => "rust",
            ".java"       => "java",
            ".kt"         => "kotlin",
            ".swift"      => "swift",
            ".md"         => "markdown",
            ".json"       => "json",
            ".yaml"       => "yaml",
            ".yml"        => "yaml",
            ".xml"        => "xml",
            ".html"       => "html",
            ".css"        => "css",
            ".scss"       => "scss",
            ".sql"        => "sql",
            ".sh"         => "bash",
            ".bash"       => "bash",
            ".zsh"        => "bash",
            ".razor"      => "razor",
            ".toml"       => "toml",
            ".dockerfile" => "dockerfile",
            _             => ""
        };
    }
}
