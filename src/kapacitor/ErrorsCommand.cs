using System.Text.Json;

namespace kapacitor;

static class ErrorsCommand {
    public static async Task<int> HandleErrors(string baseUrl, string sessionId, bool chain) {
        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        var       query      = chain ? "?chain=true" : "";

        HttpResponseMessage resp;
        try {
            resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/errors{query}");
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);
            return 1;
        }

        if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) {
            return 1;
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) {
            await Console.Error.WriteLineAsync($"Session not found: {sessionId}");
            return 1;
        }

        if (!resp.IsSuccessStatusCode) {
            await Console.Error.WriteLineAsync($"HTTP {(int)resp.StatusCode}");
            return 1;
        }

        var json   = await resp.Content.ReadAsStringAsync();
        var errors = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.ListErrorEntry);

        if (errors is null || errors.Count == 0) {
            Console.WriteLine("No errors found.");
            return 0;
        }

        Console.WriteLine($"Found {errors.Count} error(s):\n");

        foreach (var error in errors) {
            var label    = error.SessionSlug ?? error.SessionId;
            var agentTag = error.AgentId is not null ? $" (agent {error.AgentId})" : "";
            var tool     = error.ToolName ?? "unknown";
            Console.WriteLine($"  [{label}]{agentTag} #{error.EventNumber} {tool}");
            Console.WriteLine($"    {error.Error}");
            Console.WriteLine();
        }

        return 0;
    }
}