using System.Text.Json;

namespace kapacitor;

static class RecapCommand {
    public static async Task<int> HandleRecap(string baseUrl, string sessionId, bool chain) {
        using var httpClient = new HttpClient();
        var       query      = chain ? "?chain=true" : "";

        HttpResponseMessage resp;
        try {
            resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/recap{query}");
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);
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
            Console.WriteLine("No recap entries found.");
            return 0;
        }

        string? currentSessionId = null;
        string? currentAgentId   = null;

        foreach (var entry in entries) {
            // Session header in chain mode
            if (chain && entry.SessionId != currentSessionId) {
                currentSessionId = entry.SessionId;
                currentAgentId   = null;
                Console.WriteLine($"# Session {currentSessionId}");
                Console.WriteLine();
            }

            // Agent header when agent changes
            if (entry.AgentId is not null && entry.AgentId != currentAgentId) {
                currentAgentId = entry.AgentId;
                var agentLabel = entry.AgentType is not null ? $"Agent ({entry.AgentType})" : $"Agent {entry.AgentId}";
                Console.WriteLine($"### {agentLabel}");
                Console.WriteLine();
            }
            else if (entry.AgentId is null && currentAgentId is not null) {
                currentAgentId = null;
            }

            switch (entry.Type) {
                case "plan":
                    Console.WriteLine("## Plan");
                    Console.WriteLine(entry.Content);
                    Console.WriteLine();
                    break;

                case "user_prompt":
                    Console.WriteLine("## User Prompt");
                    Console.WriteLine(entry.Content);
                    Console.WriteLine();
                    break;

                case "assistant_text":
                    Console.WriteLine("## Assistant");
                    Console.WriteLine(entry.Content);
                    Console.WriteLine();
                    break;

                case "write":
                    var writePath = entry.FilePath ?? "unknown";
                    var writeLang = GetLanguageHint(writePath);
                    Console.WriteLine($"## Write {writePath}");
                    Console.WriteLine($"```{writeLang}");
                    Console.WriteLine(entry.Content);
                    Console.WriteLine("```");
                    Console.WriteLine();
                    break;

                case "edit":
                    var editPath = entry.FilePath ?? "unknown";
                    Console.WriteLine($"## Edit {editPath}");
                    Console.WriteLine("```");
                    Console.WriteLine(entry.Content);
                    Console.WriteLine("```");
                    Console.WriteLine();
                    break;
            }
        }

        return 0;
    }

    static string GetLanguageHint(string filePath) {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch {
            ".cs"    => "csharp",
            ".js"    => "javascript",
            ".ts"    => "typescript",
            ".tsx"   => "tsx",
            ".jsx"   => "jsx",
            ".py"    => "python",
            ".rb"    => "ruby",
            ".go"    => "go",
            ".rs"    => "rust",
            ".java"  => "java",
            ".kt"    => "kotlin",
            ".swift" => "swift",
            ".md"    => "markdown",
            ".json"  => "json",
            ".yaml"  => "yaml",
            ".yml"   => "yaml",
            ".xml"   => "xml",
            ".html"  => "html",
            ".css"   => "css",
            ".scss"  => "scss",
            ".sql"   => "sql",
            ".sh"    => "bash",
            ".bash"  => "bash",
            ".zsh"   => "bash",
            ".razor" => "razor",
            ".toml"  => "toml",
            ".dockerfile" => "dockerfile",
            _        => ""
        };
    }
}
