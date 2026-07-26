using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

/// <summary>
/// `kcap mcp analytics` — two read-only tools over the bearer-authed /api/analytics views:
/// the agent fetches the schema document, writes SQL, and self-repairs from the server's
/// rejection reasons. Structure cloned from McpMemoryServer.
/// </summary>
static class McpAnalyticsServer {
    internal const string NotLoggedInMessage = "Not logged in. Run 'kcap login' on the host shell.";

    internal const string NotSupportedMessage =
        "This server does not support analytics queries — upgrade kcap-server.";

    internal const string TimedOutMessage = "Query timed out — narrow the date range or aggregate.";

    internal const string SchemaTimedOutMessage = "Schema fetch timed out — retry.";

    // The query hint ("narrow the date range") is nonsense for the schema fetch, which has no
    // query to narrow — pick the message that matches the timed-out tool.
    static string TimeoutHintFor(string toolName) =>
        toolName == "get_analytics_schema" ? SchemaTimedOutMessage : TimedOutMessage;

    public static async Task<int> RunAsync(string baseUrl) {
        var cwdRepoHash = await ResolveCwdRepoHashAsync();
        var tools       = BuildToolsList();

        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // Deferred authenticated client: auto-registered and spawned every session, so startup
        // stays local-only until a tool is invoked. See McpMemoryServer for the
        // nullable-field-not-Lazy rationale.
        HttpClient? client = null;

        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                client ??= await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, cwdRepoHash);
            } catch (Exception ex) {
                await Console.Error.WriteLineAsync($"kcap mcp analytics: unexpected error handling tools/call: {ex}");
                return BuildToolResult(callId, "Error: internal error handling the request.", isError: true);
            }
        }

        await using var stdin  = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var       reader = new StreamReader(stdin, Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        writer.AutoFlush = true;

        try {
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
                var method = (request["method"] as JsonValue)?.TryGetValue<string>(out var m) == true ? m : null;

                if (id is null) continue; // notification — no response

                var response = method switch {
                    "initialize" => BuildInitializeResponse(id, request),
                    "tools/list" => BuildToolsListResponse(id, tools),
                    "tools/call" => await DispatchToolCallAsync(id, request),
                    _            => McpProtocol.TryHandleStandardMethod(method, id)
                                    ?? BuildErrorResponse(id, -32601, $"Method not found: {method}")
                };

                await writer.WriteLineAsync(response);
            }
        } finally {
            if (client is not null) {
                try { client.Dispose(); } catch {
                    /* swallow — best-effort cleanup */
                }
            }
        }

        return 0;
    }

    static async Task<string?> ResolveCwdRepoHashAsync() {
        try {
            var cwd      = Directory.GetCurrentDirectory();
            var repoInfo = await RepositoryDetection.DetectRepositoryAsync(cwd);

            if (repoInfo?.Owner is null || repoInfo.RepoName is null) return null;

            return RepoHashHelper.ComputeRepoHash(repoInfo.Owner, repoInfo.RepoName);
        } catch {
            return null;
        }
    }

    const string ServerInstructions =
        "Query Kurrent Capacitor's AI coding-agent analytics (sessions, tool/skill/token usage, cost, " +
        "commits, PRs, evals) with governed read-only SQL. Call get_analytics_schema once to learn the " +
        "views, then write Postgres SELECTs for query_analytics. Rejections include the reason — fix the " +
        "SQL and retry.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-analytics", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    internal static async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl,
            string?    cwdRepoHash
        ) {
        // `as JsonObject` (not AsObject(), which throws) so a wrong-SHAPED params/arguments —
        // e.g. an array where an object is expected — degrades to null and becomes an actionable
        // error below, rather than an InvalidOperationException masked as a generic internal error.
        var paramsNode = request["params"] as JsonObject;
        var arguments  = paramsNode?["arguments"] as JsonObject;

        // Read params.name tolerantly: a wrong-typed value (LLM clients send them) must yield a
        // clean protocol error, not a throw that the outer catch masks as an internal error.
        if (!TryGetStringArg(paramsNode, "name", out var toolName)) {
            return BuildErrorResponse(id, -32602, "Missing or invalid params.name");
        }

        try {
            using var httpResponse = toolName switch {
                "get_analytics_schema" => await SendWithRefreshRetryAsync(client, c => c.GetAsync($"{baseUrl}/api/analytics/schema")),
                "query_analytics"      => await SendWithRefreshRetryAsync(client, c => c.PostAsync($"{baseUrl}/api/analytics/query", ToJsonContent(BuildQueryBody(arguments, cwdRepoHash)))),
                _                      => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            return BuildToolResult(id, MapResponse(toolName, httpResponse.StatusCode, body, out var isError), isError);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (OperationCanceledException) {
            // Client-side HttpClient timeout (TaskCanceledException : OperationCanceledException):
            // the request never got a server 408, so give the same actionable hint the 408 path
            // does rather than letting it fall through to the generic outer "internal error".
            return BuildToolResult(id, TimeoutHintFor(toolName), isError: true);
        } catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException) {
            // A wrong-typed argument that slipped past the tolerant readers still becomes a tool
            // error the agent can react to — never the generic outer "internal error".
            return BuildToolResult(id, $"Error: malformed arguments — {ex.Message}", isError: true);
        }
    }

    /// <summary>Maps an /api/analytics response to tool-result text. Success passes the raw JSON
    /// through (plus a truncation trailer — a boolean flag alone is easy for a model to miss);
    /// error statuses become actionable, self-repair-friendly messages.</summary>
    internal static string MapResponse(string toolName, HttpStatusCode status, string body, out bool isError) {
        isError = true;

        switch (status) {
            case HttpStatusCode.OK:
                isError = false;
                return toolName == "get_analytics_schema" ? ExtractSchemaText(body) : AppendTruncationTrailer(body);
            case HttpStatusCode.Unauthorized:
                return NotLoggedInMessage;
            case HttpStatusCode.NotFound:
                return NotSupportedMessage; // server predating the /api/analytics endpoints
            case HttpStatusCode.RequestTimeout:
                return TimeoutHintFor(toolName);
            case HttpStatusCode.BadRequest:
                return $"REJECTED: {ExtractProblemDetail(body)}";
            default:
                // Unlisted statuses (e.g. a 403/429 from the app or an intermediary) still speak
                // RFC-7807 — surface the clean `detail` so the agent gets an actionable reason,
                // not a raw JSON envelope. ExtractProblemDetail falls back to the body when it
                // isn't a problem document (e.g. an HTML 502 from a proxy).
                return $"Error: HTTP {(int)status} — {ExtractProblemDetail(body)}";
        }
    }

    /// <summary>The schema endpoint wraps the document in {"text", "max_rows"} — unwrap it so the
    /// agent receives the document itself, not a JSON envelope.</summary>
    static string ExtractSchemaText(string body) {
        try {
            return JsonNode.Parse(body)?.AsObject()?["text"]?.GetValue<string>() ?? body;
        } catch {
            return body;
        }
    }

    static string AppendTruncationTrailer(string body) {
        try {
            var obj = JsonNode.Parse(body)?.AsObject();

            if (obj?["truncated"]?.GetValue<bool>() == true) {
                var maxRows = obj["max_rows"]?.GetValue<int>();
                return $"{body}\nWARNING: result truncated to {maxRows?.ToString() ?? "the server's max"} rows — " +
                       "any statistic computed from these rows is unreliable. Aggregate in SQL (GROUP BY/COUNT/AVG " +
                       "run over ALL rows server-side), add filters, or raise max_rows.";
            }
        } catch {
            // fall through to the raw body
        }

        return body;
    }

    /// <summary>400 bodies are RFC-7807 problem documents; the `detail` field carries the
    /// validator's machine-consumable rejection reason the agent self-repairs from.</summary>
    static string ExtractProblemDetail(string body) {
        try {
            return JsonNode.Parse(body)?.AsObject()?["detail"]?.GetValue<string>() ?? body;
        } catch {
            return body;
        }
    }

    static async Task<HttpResponseMessage> SendWithRefreshRetryAsync(HttpClient client, Func<HttpClient, Task<HttpResponseMessage>> send) {
        var response = await send(client);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        var refreshed = await TokenStore.GetValidTokensAsync();

        if (refreshed is null) return response; // genuinely not logged in; keep the original 401

        response.Dispose();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        return await send(client);
    }

    static StringContent ToJsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    // NOTE: request-body keys are snake_case — the server's global JSON policy.
    internal static JsonObject BuildQueryBody(JsonObject? args, string? cwdRepoHash) {
        if (!TryGetStringArg(args, "sql", out var sql))
            throw new ArgumentException("sql is required and must be a non-empty string.");

        // scope is optional (defaults to repo), but a present-yet-wrong-typed value is a mistake
        // the agent should hear about precisely rather than as an opaque failure.
        var scope = "repo";
        if (args?["scope"] is JsonNode scopeNode) {
            if (scopeNode is not JsonValue sv || !sv.TryGetValue<string>(out var scopeStr))
                throw new ArgumentException("scope must be a string — 'repo' (default) or 'global'.");
            scope = scopeStr;
        }

        // Fail closed rather than silently widening scope: an unresolvable cwd repo must never
        // default to the tenant-wide route (mirrors McpMemoryServer.BuildSaveBody).
        var repos = scope switch {
            "repo" when cwdRepoHash is null => throw new ArgumentException(
                "Cannot resolve the current repository — run from a git checkout or pass scope: 'global' to query all repositories."),
            "repo"   => new JsonArray(JsonValue.Create(cwdRepoHash)),
            "global" => null,
            _        => throw new ArgumentException($"Unknown scope: '{scope}' — use 'repo' (default) or 'global'.")
        };

        var body = new JsonObject {
            ["sql"]   = sql,
            ["repos"] = repos
        };

        if (TryReadInt(args, "max_rows", out var maxRows)) body["max_rows"] = maxRows;

        return body;
    }

    /// <summary>Reads a required string argument tolerantly: returns false (rather than throwing)
    /// when the key is absent, the wrong JSON type, or blank. Mirrors McpReviewServer.</summary>
    static bool TryGetStringArg(JsonObject? args, string name, out string value) {
        value = "";

        if (args?[name] is not JsonValue node) return false;
        if (!node.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return false;

        value = s;

        return true;
    }

    /// <summary>Reads an optional integer argument, tolerant of JSON number shapes (int/long/double)
    /// and rejecting out-of-range or non-integer values with a precise message. A private copy per
    /// the established per-server convention (kcap-sessions/-memory/-workitems each carry their own),
    /// so this server takes no compile-time dependency on another MCP server.</summary>
    static bool TryReadInt(JsonObject? args, string key, out int value) {
        value = 0;
        var node = args?[key];

        if (node is null) return false;

        JsonValue v;

        try {
            v = node.AsValue();
        } catch {
            return false; // wrong shape (object/array)
        }

        if (v.TryGetValue(out value)) return true;

        if (v.TryGetValue<long>(out var lv)) {
            if (lv is < int.MinValue or > int.MaxValue)
                throw new ArgumentException($"'{key}' value {lv} is out of range for int.");

            value = (int)lv;

            return true;
        }

        if (v.TryGetValue<double>(out var dv)) {
            var rounded = (long)dv;

            if (rounded < int.MinValue || rounded > int.MaxValue || rounded != dv)
                throw new ArgumentException($"'{key}' value {dv} is out of range or non-integer for int.");

            value = (int)rounded;

            return true;
        }

        return false;
    }

    static string BuildToolResult(JsonNode id, string text, bool isError = false) =>
        ToResponse<McpToolCallResult>(id, new([new("text", text)], isError ? true : null), McpJsonContext.Default.McpToolCallResult);

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

    internal static McpTool[] BuildToolsList() => [
        new("get_analytics_schema",
            "The governed analytics schema: the v_an_* views and columns you may query, a terminology glossary, " +
            "SQL rules, and worked examples. Always call this once before writing SQL for query_analytics. " +
            "Covers sessions, tool/skill/token usage, cost, code changes, commits, PRs, evals, and work items.",
            new("object", new(), [])),
        new("query_analytics",
            "Run one governed read-only Postgres SELECT over the analytics views (see get_analytics_schema). " +
            "Answers questions about coding-agent sessions, usage, cost, and delivery telemetry. A rejected " +
            "query returns the reason — fix the SQL and retry. Results are row-capped and flagged when truncated.",
            new("object", new() {
                ["sql"]      = new("string", "A single Postgres SELECT over the governed v_an_* views."),
                ["scope"]    = new("string", "'repo' (default) = only the current repository, derived from the working directory; 'global' = all repositories in the org."),
                ["max_rows"] = new("number", "Row cap for this query (default 300; clamped to the server's maximum).")
            }, ["sql"]))
    ];
}
