using System.Text;
using System.Text.Json.Nodes;

namespace kapacitor;

static class PermissionRequestCommand {
    public static async Task<int> Handle(string baseUrl) {
        // If not a rendered agent session, exit immediately (no-op)
        if (Environment.GetEnvironmentVariable("KAPACITOR_RENDERED_AGENT") is not "1")
            return 0;

        var body = await Console.In.ReadToEndAsync();

        // Parse the hook input to extract fields
        JsonNode? node;
        try {
            node = JsonNode.Parse(body);
        } catch {
            Console.Error.WriteLine("[kapacitor] Failed to parse permission-request input");
            return 0; // Don't block Claude Code on parse errors
        }

        if (node is null) return 0;

        var sessionId = node["session_id"]?.GetValue<string>();
        if (sessionId is null) {
            Console.Error.WriteLine("[kapacitor] No session_id in permission-request");
            return 0;
        }

        var toolName = node["tool_name"]?.GetValue<string>() ?? "Unknown";
        var toolInput = node["tool_input"];
        var suggestions = node["permission_suggestions"];

        // POST to server and wait for response (server blocks until user decides)
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        var payload = new JsonObject {
            ["session_id"] = sessionId,
            ["tool_name"] = toolName,
            ["tool_input"] = toolInput?.DeepClone(),
            ["permission_suggestions"] = suggestions?.DeepClone()
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        try {
            var response = await client.PostAsync($"{baseUrl}/hooks/permission-request", content);

            if (!response.IsSuccessStatusCode) {
                Console.Error.WriteLine($"[kapacitor] permission-request failed: HTTP {(int)response.StatusCode}");
                return 2; // Deny on server error
            }

            // Write the server response (hook JSON) directly to stdout
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.Write(responseBody);
            return 0;
        } catch (TaskCanceledException) {
            Console.Error.WriteLine("[kapacitor] permission-request timed out");
            return 2;
        } catch (HttpRequestException ex) {
            Console.Error.WriteLine($"[kapacitor] permission-request error: {ex.Message}");
            return 2; // Deny on connection error
        }
    }
}
