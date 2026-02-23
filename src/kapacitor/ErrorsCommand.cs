using System.Text.Json;

static class ErrorsCommand {
    public static async Task<int> HandleErrors(string baseUrl, string sessionId, bool chain) {
        using var httpClient = new HttpClient();
        var query = chain ? "?chain=true" : "";
        var resp = await httpClient.GetAsync($"{baseUrl}/api/sessions/{sessionId}/errors{query}");

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) {
            Console.Error.WriteLine($"Session not found: {sessionId}");
            return 1;
        }

        if (!resp.IsSuccessStatusCode) {
            Console.Error.WriteLine($"HTTP {(int)resp.StatusCode}");
            return 1;
        }

        var json = await resp.Content.ReadAsStringAsync();
        var errors = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.ListErrorEntry);

        if (errors is null || errors.Count == 0) {
            Console.WriteLine("No errors found.");
            return 0;
        }

        Console.WriteLine($"Found {errors.Count} error(s):\n");

        foreach (var error in errors) {
            var label = error.SessionSlug ?? error.SessionId;
            var agentTag = error.AgentId is not null ? $" (agent {error.AgentId})" : "";
            var tool = error.ToolName ?? "unknown";
            Console.WriteLine($"  [{label}]{agentTag} #{error.EventNumber} {tool}");
            Console.WriteLine($"    {error.Error}");
            Console.WriteLine();
        }

        return 0;
    }
}
