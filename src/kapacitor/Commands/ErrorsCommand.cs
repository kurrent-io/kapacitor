using System.Text.Json;
// ReSharper disable MethodHasAsyncOverload

namespace kapacitor.Commands;

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
            Console.Error.WriteLine($"Session not found: {sessionId}");

            return 1;
        }

        if (!resp.IsSuccessStatusCode) {
            Console.Error.WriteLine($"HTTP {(int)resp.StatusCode}");

            return 1;
        }

        var json   = await resp.Content.ReadAsStringAsync();
        var errors = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.ListErrorEntry);

        if (errors is null || errors.Count == 0) {
            await Console.Out.WriteLineAsync("No errors found.");

            return 0;
        }

        await Console.Out.WriteLineAsync($"Found {errors.Count} error(s):\n");

        foreach (var error in errors) {
            var label    = error.SessionSlug ?? error.SessionId;
            var agentTag = error.AgentId is not null ? $" (agent {error.AgentId})" : "";
            var tool     = error.ToolName ?? "unknown";
            await Console.Out.WriteLineAsync($"  [{label}]{agentTag} #{error.EventNumber} {tool}");
            await Console.Out.WriteLineAsync($"    {error.Error}");
            await Console.Out.WriteLineAsync();
        }

        return 0;
    }
}
