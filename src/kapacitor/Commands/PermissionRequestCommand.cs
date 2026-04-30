using System.Text;
using System.Text.Json.Nodes;
// ReSharper disable MethodHasAsyncOverload

namespace kapacitor.Commands;

static class PermissionRequestCommand {
    public static async Task<int> Handle(string baseUrl) {
        var body = await Console.In.ReadToEndAsync();

        JsonNode? node;

        try {
            node = JsonNode.Parse(body);
        } catch {
            Console.Error.WriteLine("[kapacitor] Failed to parse permission-request input");

            return 0;
        }

        if (node is null)
            return 0;

        var sessionId = node["session_id"]?.GetValue<string>()?.Replace("-", "");

        if (sessionId is null) {
            Console.Error.WriteLine("[kapacitor] No session_id in permission-request");

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

        var payload = new JsonObject {
            ["session_id"]             = sessionId,
            ["tool_name"]              = toolName,
            ["tool_input"]             = toolInput?.DeepClone(),
            ["permission_suggestions"] = suggestions?.DeepClone()
        };

        // Prefer the daemon's local SignalR bridge when present — that path runs the
        // long-poll over the daemon's persistent SignalR connection, bypassing
        // Cloudflare's HTTP-request timeout (~120s) that severs the equivalent route
        // on the server. Older daemon builds don't set this env var, so we fall back
        // to the original /hooks/permission-request HTTPS path.
        var daemonUrl = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");

        return daemonUrl is { Length: > 0 } d
            ? await PostAsync(d + "/permission-request", payload, authenticated: false)
            : await PostAsync(baseUrl + "/hooks/permission-request", payload, authenticated: true);
    }

    static async Task<int> PostAsync(string url, JsonObject payload, bool authenticated) {
        using var client = authenticated
            ? await HttpClientExtensions.CreateAuthenticatedClientAsync()
            : new HttpClient();

        // The server-side long-poll waits up to 10 hours for the user to decide; the
        // daemon-side bridge inherits that bound transparently. Add a minute of slack
        // so the HTTP client doesn't tear down a still-pending request first.
        client.Timeout = TimeSpan.FromHours(10) + TimeSpan.FromMinutes(1);

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        try {
            using var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode) {
                Console.Error.WriteLine($"[kapacitor] permission-request failed: HTTP {(int)response.StatusCode}");

                return 2;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            Console.Write(responseBody);

            return 0;
        } catch (TaskCanceledException) {
            Console.Error.WriteLine("[kapacitor] permission-request timed out");

            return 2;
        } catch (HttpRequestException ex) {
            Console.Error.WriteLine($"[kapacitor] permission-request error: {ex.Message}");

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
            ["tool_input"] = toolInput?.DeepClone()
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
