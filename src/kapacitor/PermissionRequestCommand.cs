using System.Text;
using System.Text.Json.Nodes;

namespace kapacitor;

static class PermissionRequestCommand {
    public static async Task<int> Handle(string baseUrl) {
        var body = await Console.In.ReadToEndAsync();

        JsonNode? node;

        try {
            node = JsonNode.Parse(body);
        } catch {
            await Console.Error.WriteLineAsync("[kapacitor] Failed to parse permission-request input");
            return 0;
        }

        if (node is null)
            return 0;

        var sessionId = node["session_id"]?.GetValue<string>()?.Replace("-", "");
        if (sessionId is null) {
            await Console.Error.WriteLineAsync("[kapacitor] No session_id in permission-request");
            return 0;
        }

        var isRenderedAgent = Environment.GetEnvironmentVariable("KAPACITOR_RENDERED_AGENT") is "1";

        if (isRenderedAgent) {
            return await HandleRenderedAgent(baseUrl, node, sessionId);
        }

        // Non-rendered agent: record the permission event and return immediately
        return await HandleRecordOnly(baseUrl, node, sessionId);
    }

    static async Task<int> HandleRenderedAgent(string baseUrl, JsonNode node, string sessionId) {
        var toolName    = node["tool_name"]?.GetValue<string>() ?? "Unknown";
        var toolInput   = node["tool_input"];
        var suggestions = node["permission_suggestions"];

        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        client.Timeout = TimeSpan.FromHours(10) + TimeSpan.FromMinutes(1);

        var payload = new JsonObject {
            ["session_id"]             = sessionId,
            ["tool_name"]              = toolName,
            ["tool_input"]             = toolInput?.DeepClone(),
            ["permission_suggestions"] = suggestions?.DeepClone()
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        try {
            using var response = await client.PostAsync($"{baseUrl}/hooks/permission-request", content);

            if (!response.IsSuccessStatusCode) {
                await Console.Error.WriteLineAsync($"[kapacitor] permission-request failed: HTTP {(int)response.StatusCode}");
                return 2;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            Console.Write(responseBody);
            return 0;
        } catch (TaskCanceledException) {
            await Console.Error.WriteLineAsync("[kapacitor] permission-request timed out");
            return 2;
        } catch (HttpRequestException ex) {
            await Console.Error.WriteLineAsync($"[kapacitor] permission-request error: {ex.Message}");
            return 2;
        }
    }

    static async Task<int> HandleRecordOnly(string baseUrl, JsonNode node, string sessionId) {
        var toolName  = node["tool_name"]?.GetValue<string>() ?? "Unknown";
        var toolInput = node["tool_input"];

        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        client.Timeout = TimeSpan.FromSeconds(2);

        var payload = new JsonObject {
            ["session_id"] = sessionId,
            ["tool_name"]  = toolName,
            ["tool_input"]  = toolInput?.DeepClone()
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        try {
            using var response = await client.PostAsync($"{baseUrl}/hooks/permission-record", content);
        } catch {
            // Silently ignore — don't block Claude Code for recording failures
        }

        return 0;
    }
}
