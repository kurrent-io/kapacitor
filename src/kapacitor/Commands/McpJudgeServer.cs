using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace kapacitor.Commands;

static class McpJudgeServer {
    /// <summary>
    /// Run as a session-scoped MCP server. All tool calls must use <paramref name="expectedSessionId"/>.
    /// </summary>
    public static async Task<int> RunAsync(string baseUrl, string expectedSessionId) {
        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);

        var tools = BuildToolsList();

        await using var stdin  = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var       reader = new StreamReader(stdin, Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        writer.AutoFlush = true;

        while (await reader.ReadLineAsync() is { } line) {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonObject? request;

            try {
                request = JsonNode.Parse(line)?.AsObject();
            } catch {
                continue; // skip malformed JSON
            }

            if (request is null) continue;

            var id     = request["id"];
            var method = request["method"]?.GetValue<string>();

            // Notifications have no id — don't send a response
            if (id is null) continue;

            var response = method switch {
                "initialize" => BuildInitializeResponse(id),
                "tools/list" => BuildToolsListResponse(id, tools),
                "tools/call" => await HandleToolCallAsync(id, request, client, baseUrl, expectedSessionId),
                _            => BuildErrorResponse(id, -32601, $"Method not found: {method}")
            };

            await writer.WriteLineAsync(response);
        }

        return 0;
    }

    /// <summary>Test hook: execute a tool-call handler and return the JSON-RPC envelope.</summary>
    internal static async Task<string> HandleToolCallForTests(
            string     toolName,
            JsonObject arguments,
            HttpClient client,
            string     baseUrl,
            string     expectedSessionId
        ) {
        JsonNode id = 1;

        var request = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["method"]  = "tools/call",
            ["params"] = new JsonObject {
                ["name"]      = toolName,
                ["arguments"] = arguments.DeepClone()
            }
        };

        return await HandleToolCallAsync(id, request, client, baseUrl, expectedSessionId);
    }

    static async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl,
            string     expectedSessionId
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            var prBase    = baseUrl.TrimEnd('/');
            var sessionId = arguments?["session_id"]?.GetValue<string>();

            if (sessionId is null) {
                return BuildToolResult(id, "Error: missing required argument: session_id", isError: true);
            }

            if (sessionId != expectedSessionId) {
                return BuildToolResult(
                    id,
                    $"Error: session_id '{sessionId}' does not match this judge's bound session",
                    isError: true
                );
            }

            var encoded = Uri.EscapeDataString(sessionId);

            var httpResponse = toolName switch {
                "get_session_recap" => await client.GetAsync($"{prBase}/api/sessions/{encoded}/recap?chain=true"),
                _                   => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            return !httpResponse.IsSuccessStatusCode
                ? BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true)
                : BuildToolResult(id, body);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static string BuildInitializeResponse(JsonNode id) =>
        ToResponse(
            id,
            new("2024-11-05", new(new()), new("kapacitor-judge", "1.0.0")),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    static string BuildToolResult(JsonNode id, string text, bool isError = false) =>
        ToResponse(id, new([new("text", text)], isError ? true : null), McpJsonContext.Default.McpToolCallResult);

    static string BuildErrorResponse(JsonNode id, int code, string message) {
        var envelope = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["error"]   = JsonSerializer.SerializeToNode(new McpError(code, message), McpJsonContext.Default.McpError)
        };

        return envelope.ToJsonString();
    }

    static string ToResponse<T>(JsonNode id, T result, JsonTypeInfo<T> typeInfo) {
        var envelope = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["result"]  = JsonSerializer.SerializeToNode(result, typeInfo)
        };

        return envelope.ToJsonString();
    }

    static McpTool[] BuildToolsList() => [
        new(
            "get_session_recap",
            "Get a short narrative recap of the session's user inputs, assistant replies, and tool invocations. "
          + "Start here — it's the cheapest way to orient yourself before pulling specific transcript slices.",
            new(
                "object",
                new() { ["session_id"] = new("string", "Session ID to recap (must match the judge's bound session)") },
                ["session_id"]
            )
        )
    ];
}
