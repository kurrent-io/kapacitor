using System.Text;
using System.Text.Json.Nodes;
using kapacitor.Auth;

namespace kapacitor.Commands;

static class McpReviewServer {
    public static async Task<int> RunAsync(string baseUrl, string owner, string repo, int prNumber) {
        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);

        var tools = BuildToolsList();

        using var stdin  = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(stdin, Encoding.UTF8);
        using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };

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

            JsonObject response = method switch {
                "initialize"  => BuildInitializeResponse(id),
                "tools/list"  => BuildToolsListResponse(id, tools),
                "tools/call"  => await HandleToolCallAsync(id, request, client, baseUrl, owner, repo, prNumber),
                _             => BuildErrorResponse(id, -32601, $"Method not found: {method}")
            };

            await writer.WriteLineAsync(response.ToJsonString());
        }

        return 0;
    }

    static JsonObject BuildInitializeResponse(JsonNode id) =>
        new() {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["result"] = new JsonObject {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject {
                    ["tools"] = new JsonObject()
                },
                ["serverInfo"] = new JsonObject {
                    ["name"]    = "kapacitor-review",
                    ["version"] = "1.0.0"
                }
            }
        };

    static JsonObject BuildToolsListResponse(JsonNode id, JsonArray tools) =>
        new() {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["result"] = new JsonObject {
                ["tools"] = tools.DeepClone()
            }
        };

    static async Task<JsonObject> HandleToolCallAsync(
        JsonNode   id,
        JsonObject request,
        HttpClient client,
        string     baseUrl,
        string     owner,
        string     repo,
        int        prNumber
    ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            var prBase = $"{baseUrl}/api/review/{owner}/{repo}/pulls/{prNumber}";

            HttpResponseMessage response = toolName switch {
                "get_pr_summary" => await client.GetAsync(prBase),
                "list_pr_files"  => await client.GetAsync($"{prBase}/files"),
                "get_file_context" => await client.GetAsync(
                    $"{prBase}/files/{GetRequiredArg(arguments, "file_path").TrimStart('/')}"
                ),
                "search_context" => await client.PostAsync(
                    $"{prBase}/search",
                    new StringContent(
                        new JsonObject { ["query"] = GetRequiredArg(arguments, "query") }.ToJsonString(),
                        Encoding.UTF8,
                        "application/json"
                    )
                ),
                "list_sessions" => await client.GetAsync($"{prBase}/sessions"),
                "get_transcript" => await client.GetAsync(
                    BuildTranscriptUrl(baseUrl, arguments)
                ),
                _ => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode) {
                return BuildToolResult(id, $"Error: HTTP {(int)response.StatusCode} — {body}", isError: true);
            }

            return BuildToolResult(id, body);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static string GetRequiredArg(JsonObject? arguments, string name) {
        var value = arguments?[name]?.GetValue<string>();

        if (value is null) {
            throw new ArgumentException($"Missing required argument: {name}");
        }

        return value;
    }

    static string BuildTranscriptUrl(string baseUrl, JsonObject? arguments) {
        var sessionId = arguments?["session_id"]?.GetValue<string>()
         ?? throw new ArgumentException("Missing required argument: session_id");

        var url = $"{baseUrl}/api/review/sessions/{Uri.EscapeDataString(sessionId)}/transcript";
        var queryParams = new List<string>();

        if (arguments?["file_path"]?.GetValue<string>() is { } filePath) {
            queryParams.Add($"file_path={Uri.EscapeDataString(filePath)}");
        }

        if (arguments?["skip"]?.ToString() is { } skip) {
            queryParams.Add($"skip={Uri.EscapeDataString(skip)}");
        }

        if (arguments?["take"]?.ToString() is { } take) {
            queryParams.Add($"take={Uri.EscapeDataString(take)}");
        }

        if (queryParams.Count > 0) {
            url += "?" + string.Join("&", queryParams);
        }

        return url;
    }

    static JsonObject BuildToolResult(JsonNode id, string text, bool isError = false) {
        var contentItem = new JsonObject { ["type"] = "text", ["text"] = text };
        var result = new JsonObject {
            ["content"] = new JsonArray(contentItem)
        };

        if (isError) {
            result["isError"] = true;
        }

        return new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["result"]  = result
        };
    }

    static JsonObject BuildErrorResponse(JsonNode id, int code, string message) =>
        new() {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["error"] = new JsonObject {
                ["code"]    = code,
                ["message"] = message
            }
        };

    static JsonArray BuildToolsList() =>
        [
            BuildToolDef(
                "get_pr_summary",
                "Get an overview of a pull request including sessions, files changed, and test runs",
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray() }
            ),
            BuildToolDef(
                "list_pr_files",
                "List files changed in the pull request with links to the sessions that changed them",
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray() }
            ),
            BuildToolDef(
                "get_file_context",
                "Get context about why a specific file was changed, including relevant transcript excerpts",
                new JsonObject {
                    ["type"] = "object",
                    ["properties"] = new JsonObject {
                        ["file_path"] = new JsonObject {
                            ["type"]        = "string",
                            ["description"] = "Path of the file to get context for"
                        }
                    },
                    ["required"] = new JsonArray(JsonValue.Create("file_path"))
                }
            ),
            BuildToolDef(
                "search_context",
                "Search session transcripts for relevant context using a free-text query",
                new JsonObject {
                    ["type"] = "object",
                    ["properties"] = new JsonObject {
                        ["query"] = new JsonObject {
                            ["type"]        = "string",
                            ["description"] = "Free-text search query"
                        }
                    },
                    ["required"] = new JsonArray(JsonValue.Create("query"))
                }
            ),
            BuildToolDef(
                "list_sessions",
                "List sessions associated with this pull request",
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray() }
            ),
            BuildToolDef(
                "get_transcript",
                "Get transcript events from a specific session, optionally filtered by file path",
                new JsonObject {
                    ["type"] = "object",
                    ["properties"] = new JsonObject {
                        ["session_id"] = new JsonObject {
                            ["type"]        = "string",
                            ["description"] = "Session ID to retrieve the transcript for"
                        },
                        ["file_path"] = new JsonObject {
                            ["type"]        = "string",
                            ["description"] = "Optional file path to filter transcript events"
                        },
                        ["skip"] = new JsonObject {
                            ["type"]        = "integer",
                            ["description"] = "Number of events to skip (for pagination)"
                        },
                        ["take"] = new JsonObject {
                            ["type"]        = "integer",
                            ["description"] = "Number of events to return (for pagination)"
                        }
                    },
                    ["required"] = new JsonArray(JsonValue.Create("session_id"))
                }
            )
        ];

    static JsonObject BuildToolDef(string name, string description, JsonObject inputSchema) =>
        new() {
            ["name"]        = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema
        };
}
