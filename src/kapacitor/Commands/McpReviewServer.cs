using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace kapacitor.Commands;

static class McpReviewServer {
    /// <summary>
    /// Run with explicit PR context (used by `kapacitor review`).
    /// </summary>
    public static Task<int> RunAsync(string baseUrl, string owner, string repo, int prNumber)
        => RunCoreAsync(baseUrl, owner, repo, prNumber);

    /// <summary>
    /// Run with auto-detection from git/gh in current directory (used by plugin MCP server).
    /// </summary>
    public static Task<int> RunAutoAsync(string baseUrl)
        => RunCoreAsync(baseUrl, null, null, null);

    static async Task<int> RunCoreAsync(string baseUrl, string? owner, string? repo, int? prNumber) {
        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);

        // Auto-detect from git if not provided
        if (owner is null || repo is null || prNumber is null) {
            var detected = await DetectPrFromGitAsync();

            if (detected is not null) {
                owner    ??= detected.Value.Owner;
                repo     ??= detected.Value.Repo;
                prNumber ??= detected.Value.PrNumber;
            }
        }

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

            string response;

            if (method == "tools/call" && (owner is null || repo is null || prNumber is null)) {
                response = BuildToolResult(
                    id,
                    "No PR detected for current branch. Either:\n"                      +
                    "- Switch to a branch with an open PR and restart the MCP server\n" +
                    "- Use `kapacitor review <pr>` to launch a dedicated review session",
                    isError: true
                );
            } else {
                response = method switch {
                    "initialize" => BuildInitializeResponse(id),
                    "tools/list" => BuildToolsListResponse(id, tools),
                    "tools/call" => await HandleToolCallAsync(id, request, client, baseUrl, owner!, repo!, prNumber!.Value),
                    _            => BuildErrorResponse(id, -32601, $"Method not found: {method}")
                };
            }

            await writer.WriteLineAsync(response);
        }

        return 0;
    }

    static async Task<(string Owner, string Repo, int PrNumber)?> DetectPrFromGitAsync() {
        try {
            var cwd      = Directory.GetCurrentDirectory();
            var repoInfo = await RepositoryDetection.DetectRepositoryAsync(cwd);

            if (repoInfo?.Owner is not null && repoInfo.RepoName is not null && repoInfo.PrNumber is not null) {
                return (repoInfo.Owner, repoInfo.RepoName, repoInfo.PrNumber.Value);
            }

            return null;
        } catch {
            return null;
        }
    }

    static string BuildInitializeResponse(JsonNode id) =>
        ToResponse(id, new McpInitResult(
            "2024-11-05",
            new McpCapabilities(new McpToolsCapability()),
            new McpServerInfo("kapacitor-review", "1.0.0")
        ), McpJsonContext.Default.McpInitResult);

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    static async Task<string> HandleToolCallAsync(
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

            HttpResponseMessage httpResponse = toolName switch {
                "get_pr_summary" => await client.GetAsync(prBase),
                "list_pr_files"  => await client.GetAsync($"{prBase}/files"),
                "get_file_context" => await client.GetAsync(
                    $"{prBase}/files/{GetRequiredArg(arguments, "file_path").TrimStart('/')}"
                ),
                "search_context" => await client.PostAsync(
                    $"{prBase}/search",
                    JsonContent.Create(
                        new SearchQuery(GetRequiredArg(arguments, "query")),
                        McpJsonContext.Default.SearchQuery
                    )
                ),
                "list_sessions" => await client.GetAsync($"{prBase}/sessions"),
                "get_transcript" => await client.GetAsync(
                    BuildTranscriptUrl(baseUrl, arguments)
                ),
                _ => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (!httpResponse.IsSuccessStatusCode) {
                return BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true);
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

        var url         = $"{baseUrl}/api/review/sessions/{Uri.EscapeDataString(sessionId)}/transcript";
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

    static string BuildToolResult(JsonNode id, string text, bool isError = false) =>
        ToResponse(id, new McpToolCallResult(
            [new McpContentItem("text", text)],
            isError ? true : null
        ), McpJsonContext.Default.McpToolCallResult);

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
            "get_pr_summary",
            "Get an overview of the PR: which Claude Code sessions contributed, which files were changed (with event counts), and what test commands were run with their pass/fail outcomes. Call this first to orient yourself.",
            new McpInputSchema("object", new(), [])
        ),
        new(
            "list_pr_files",
            "List all files changed in the PR with aggregated metadata: change types (read/edit/create), how many sessions touched each file, and total event count. Use this to understand the scope of changes.",
            new McpInputSchema("object", new(), [])
        ),
        new(
            "get_file_context",
            "Get deep context for a specific file: which sessions modified it, when, and relevant transcript excerpts where the file was discussed or changed. Use this when a reviewer asks 'why was this file changed?'",
            new McpInputSchema("object", new() {
                ["file_path"] = new McpSchemaProperty("string", "Path of the file to get context for")
            }, ["file_path"])
        ),
        new(
            "search_context",
            "Full-text search across all session transcripts linked to this PR. Returns ranked excerpts with speaker (user/assistant/tool), content, and highlighted snippets. Use for 'why' questions: 'why retry logic', 'what alternatives', 'error handling rationale'.",
            new McpInputSchema("object", new() {
                ["query"] = new McpSchemaProperty("string", "Free-text search query")
            }, ["query"])
        ),
        new(
            "list_sessions",
            "List all Claude Code sessions that contributed to this PR, with session IDs, titles, timestamps, and models used. Use this to understand the work timeline and pick sessions to drill into with get_transcript.",
            new McpInputSchema("object", new(), [])
        ),
        new(
            "get_transcript",
            "Get the full transcript of a specific session: user messages, assistant reasoning, tool calls, and results. Paginated (default 100 events). Use file_path filter to scope to events mentioning a specific file. This is the deepest level of detail — use when you need to trace the exact reasoning chain.",
            new McpInputSchema("object", new() {
                ["session_id"] = new McpSchemaProperty("string", "Session ID to retrieve the transcript for"),
                ["file_path"]  = new McpSchemaProperty("string", "Optional file path to filter transcript events"),
                ["skip"]       = new McpSchemaProperty("integer", "Number of events to skip (for pagination)"),
                ["take"]       = new McpSchemaProperty("integer", "Number of events to return (for pagination)")
            }, ["session_id"])
        )
    ];
}

// MCP protocol types — serialized with source-generated McpJsonContext for AOT compatibility
record McpInitResult(string ProtocolVersion, McpCapabilities Capabilities, McpServerInfo ServerInfo);
record McpCapabilities(McpToolsCapability Tools);
record McpToolsCapability;
record McpServerInfo(string Name, string Version);
record McpToolsResult(McpTool[] Tools);
record McpTool(string Name, string Description, McpInputSchema InputSchema);
record McpInputSchema(string Type, Dictionary<string, McpSchemaProperty> Properties, string[] Required);
record McpSchemaProperty(string Type, string Description);
record McpToolCallResult(McpContentItem[] Content, bool? IsError = null);
record McpContentItem(string Type, string Text);
record McpError(int Code, string Message);
record SearchQuery(string Query);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McpInitResult))]
[JsonSerializable(typeof(McpToolsResult))]
[JsonSerializable(typeof(McpToolCallResult))]
[JsonSerializable(typeof(McpError))]
[JsonSerializable(typeof(SearchQuery))]
partial class McpJsonContext : JsonSerializerContext;
