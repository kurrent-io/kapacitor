using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

/// <summary> P2 task 17: MCP tools for the work-items correlation surface — attach the
/// current session (and its continuation chain) to a work item, and list what a session is
/// already attached to. Cloned from <see cref="McpMemoryServer"/>'s stdio JSON-RPC loop; unlike
/// memory this server has no repo/machine context to resolve — the only per-call input is the
/// session id and the declare selector, both carried in the tool arguments.</summary>
static class McpWorkItemsServer {
    internal const string NotLoggedInMessage = "Not logged in. Run 'kcap login' on the host shell.";

    internal const string NoSessionIdMessage =
        "No session id: pass session_id explicitly or run inside a kcap-hooked session (KCAP_SESSION_ID or CODEX_THREAD_ID).";

    public static async Task<int> RunAsync(string baseUrl) {
        var tools = BuildToolsList();

        // Validate the server_url shape once, locally (pure string check — no network, token,
        // or stderr). Used to fail gracefully instead of hard-exiting mid-request (below).
        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // Created on demand (not at startup) so a session that never calls a tool pays no
        // network/token/stderr cost. Nullable field rather than Lazy<Task> so a transient
        // creation failure leaves it null and the next call retries. Safe without locking:
        // the stdio loop handles one request at a time.
        HttpClient? client = null;

        // Guarded tool dispatch: never let the stdio JSON-RPC loop die on one bad request. An
        // unusable server_url would otherwise reach EnsureAbsolute inside the auth-client factory,
        // which hard-exits the process (Environment.Exit(2)) mid-request; and an unexpected
        // failure would bubble out of the loop. Return a JSON-RPC tool error in both cases so the
        // server keeps serving.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                client ??= await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl, autoRetryUnauthorized: false);
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp workitems: unexpected error handling tools/call: {ex}");
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
                var method = DecodeMethod(request);

                // Notifications have no id — don't send a response
                if (id is null) continue;

                var response = method switch {
                    null         => BuildErrorResponse(id, -32600, "Invalid request: method must be a string"),
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

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-workitems", "1.0.0")),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    internal static async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            using var httpResponse = toolName switch {
                "declare_work_item"      => await SendWithRefreshRetryAsync(client, c => c.PostAsync($"{baseUrl}/api/work-items/declare", ToJsonContent(BuildDeclareBody(arguments)))),
                "get_session_work_items" => await SendWithRefreshRetryAsync(client, c => c.GetAsync(BuildSessionUrl(baseUrl, arguments))),
                _                        => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized) {
                return BuildToolResult(id, NotLoggedInMessage, isError: true);
            }

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

    /// <summary>
    /// Sends an HTTP request with one-shot retry on 401. The MCP server reuses a single
    /// <see cref="HttpClient"/> for the lifetime of the agent session, so a cached token
    /// that was valid at startup may have expired by the time a tool call is made. On 401
    /// we ask <see cref="TokenStore.GetValidTokensAsync"/> for a fresh token (which triggers
    /// the refresh flow for WorkOS / GitHubApp), update the client's <c>Authorization</c>
    /// header, and retry the same request once. If refresh fails (genuinely not logged in
    /// or refresh-token expired), the original 401 is returned and the caller surfaces the
    /// friendly "Not logged in" message.
    /// </summary>
    static async Task<HttpResponseMessage> SendWithRefreshRetryAsync(HttpClient client, Func<HttpClient, Task<HttpResponseMessage>> send) {
        var response = await send(client);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        // Force a refresh against the token this client actually sent: the 401 proves the server
        // rejected it even though it may still look unexpired locally, which a plain load would
        // not heal. Passing the rejected token also means a peer process that already refreshed is
        // adopted rather than rotated a second time. With no token attached at all — this MCP
        // process outlives a `kcap login` that finished after the client was built — there is
        // nothing to refresh, so just pick up whatever is stored now.
        var rejected = client.DefaultRequestHeaders.Authorization?.Parameter;

        // A failed rotation must not be worse than no rotation: fall back to whatever is stored so
        // the pre-existing "re-read and resend once" recovery still happens.
        var refreshed = rejected is null
            ? await TokenStore.GetValidTokensAsync()
            : await TokenStore.ForceRefreshAsync(rejected) ?? await TokenStore.GetValidTokensAsync();

        if (refreshed is null) return response; // genuinely not logged in; keep the original 401

        response.Dispose();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        return await send(client);
    }

    static StringContent ToJsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>
    /// Resolves the session id to act on: an explicit <c>session_id</c> tool argument wins,
    /// else the ambient <c>KCAP_SESSION_ID</c> (or <c>CODEX_THREAD_ID</c>) env var via
    /// <see cref="ArgParsing.ResolveSessionIdFromEnv"/>. Throws when neither is available, so
    /// the caller (via <see cref="HandleToolCallAsync"/>) surfaces a clean tool error instead
    /// of sending a request with a missing/blank session id. Dashes are stripped from the
    /// explicit argument too — matching <see cref="ArgParsing.ResolveSessionIdFromEnv"/> — so a
    /// caller passing a dashed GUID (e.g. copy-pasted from a UI) still resolves to the same
    /// dashless key the server expects, instead of silently missing the intended session.
    /// </summary>
    internal static string ResolveSessionId(JsonObject? args) {
        if (args?["session_id"]?.GetValue<string>() is { Length: > 0 } explicitId) return explicitId.Replace("-", "");
        if (ArgParsing.ResolveSessionIdFromEnv() is { Length: > 0 } fromEnv) return fromEnv;

        throw new ArgumentException(NoSessionIdMessage);
    }

    // NOTE: request bodies use snake_case keys — the server's global JSON policy is
    // JsonNamingPolicy.SnakeCaseLower. Responses are passed through as raw
    // text, so only this request-body builder is affected. The server enforces "exactly one of
    // issue_key/pr_number/work_item_id/new_title" (400 on violation) — this builder passes
    // through whichever selector(s) were supplied and lets that validation surface as a tool
    // error via the 4xx-body mapping in HandleToolCallAsync, rather than duplicating the rule
    // client-side.
    internal static JsonObject BuildDeclareBody(JsonObject? args) {
        var body = new JsonObject { ["session_id"] = ResolveSessionId(args) };

        if (args?["issue_key"]?.GetValue<string>() is { Length: > 0 } issueKey) body["issue_key"] = issueKey;
        if (args?["work_item_id"]?.GetValue<string>() is { Length: > 0 } workItemId) body["work_item_id"] = workItemId;
        if (args?["new_title"]?.GetValue<string>() is { Length: > 0 } newTitle) body["new_title"] = newTitle;
        if (TryReadInt(args, "pr_number", out var prNumber)) body["pr_number"] = prNumber;

        return body;
    }

    internal static string BuildSessionUrl(string baseUrl, JsonObject? args) =>
        $"{baseUrl}/api/work-items/session/{Uri.EscapeDataString(ResolveSessionId(args))}";

    /// <summary>Decodes the JSON-RPC <c>method</c> field, returning null for a present but
    /// wrong-shaped value (e.g. an object) instead of throwing — a malformed request must yield
    /// an invalid-request response, never terminate the stdio loop.</summary>
    internal static string? DecodeMethod(JsonObject request) {
        try {
            return request["method"]?.GetValue<string>();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Reads a numeric field as int. Returns false ONLY when the key is absent (or JSON null) —
    /// any PRESENT non-integer shape (string, object, array, fractional or out-of-range number)
    /// throws <see cref="ArgumentException"/> so the caller surfaces a validation error instead
    /// of silently dropping the selector: a malformed two-selector declare (e.g. issue_key plus
    /// a string pr_number) must fail, not degrade into a "valid" single-selector attach. Wire
    /// JSON (JsonElement-backed) is validated against the RAW token via TryGetInt32 — exact, no
    /// lossy double round-trip, so a fractional part below double precision still rejects;
    /// int/long branches cover programmatically constructed nodes.
    /// </summary>
    internal static bool TryReadInt(JsonObject? args, string key, out int value) {
        value = 0;
        var node = args?[key];

        if (node is null) return false;

        if (node is JsonValue v) {
            if (v.TryGetValue<JsonElement>(out var el)) {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return true;

                throw new ArgumentException($"'{key}' must be an integer within int range.");
            }

            if (v.TryGetValue(out value)) return true;

            if (v.TryGetValue<long>(out var lv)) {
                if (lv is < int.MinValue or > int.MaxValue)
                    throw new ArgumentException($"'{key}' value {lv} is out of range for int.");

                value = (int)lv;

                return true;
            }
        }

        throw new ArgumentException($"'{key}' must be an integer.");
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
        new("declare_work_item",
            "Attach the CURRENT session (and its continuation chain) to a work item on the Capacitor server. Provide exactly one of issue_key, pr_number, work_item_id, or new_title.",
            new("object", new() {
                ["issue_key"]    = new("string", "Attach to the work item for this issue key (e.g. 'PROJ-1234'), creating it if none exists yet."),
                ["pr_number"]    = new("integer", "Attach to the work item for this PR number, creating it if none exists yet."),
                ["work_item_id"] = new("string", "Attach directly to this work item id."),
                ["new_title"]    = new("string", "Create a brand-new work item with this title and attach to it."),
                ["session_id"]   = new("string", "Session id to attach. Defaults to the current kcap-hooked session (KCAP_SESSION_ID) when omitted.")
            }, [])),
        new("get_session_work_items",
            "List the work items the current session is attached to.",
            new("object", new() {
                ["session_id"] = new("string", "Session id to look up. Defaults to the current kcap-hooked session (KCAP_SESSION_ID) when omitted.")
            }, []))
    ];
}
