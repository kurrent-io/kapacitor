using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

static class McpSessionsServer {
    internal const string NotLoggedInMessage = "Not logged in. Run 'kcap login' on the host shell.";

    public static async Task<int> RunAsync(string baseUrl) {
        var cwdRepoHash = await ResolveCwdRepoHashAsync();
        var tools       = BuildToolsList();

        // Validate the server_url shape once, locally (pure string check — no network, token,
        // or stderr). Used to fail gracefully instead of hard-exiting mid-request (below).
        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // The authenticated client is created on the first tools/call, not at startup:
        // kcap-sessions auto-registers, so Claude Code spawns `kcap mcp sessions` for every
        // session — deferring keeps startup local-only (no GET /auth/config, token load, or
        // stderr) for sessions that never invoke a tool. Created on demand into a nullable field
        // (rather than a Lazy<Task>) so a transient creation failure leaves it null and the next
        // call retries, instead of a faulted task sticking for the rest of the session. Safe
        // without locking: the stdio loop handles one request at a time.
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
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, cwdRepoHash);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp sessions: unexpected error handling tools/call: {ex}");
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
                var method = request["method"]?.GetValue<string>();

                // Notifications have no id — don't send a response
                if (id is null) continue;

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

    // Server-level usage preamble (MCP `instructions`) — steers clients toward these tools for
    // prior-work / why / who-decided questions over native grep / git log.
    const string ServerInstructions =
        "Use these tools to recall prior work — 'have we done X before', 'why did we', 'who decided Y', " +
        "'when did we work on Z'. Search here before grepping the code or git log — they search the reasoning " +
        "across past sessions, not just the code.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-sessions", "1.0.0"), ServerInstructions),
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
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            using var httpResponse = toolName switch {
                "search_sessions"        => await SendWithRefreshRetryAsync(client, baseUrl, c => c.GetAsync(BuildSearchUrl(baseUrl, arguments, cwdRepoHash))),
                "get_session_summary"    => await SendWithRefreshRetryAsync(client, baseUrl, c => c.GetAsync(BuildSummaryUrl(baseUrl, arguments))),
                "get_session_transcript" => await SendWithRefreshRetryAsync(client, baseUrl, c => c.GetAsync(BuildTranscriptUrl(baseUrl, arguments))),
                "get_turn"               => await SendWithRefreshRetryAsync(client, baseUrl, c => c.GetAsync(BuildTurnDetailUrl(baseUrl, arguments))),
                "list_turns"             => await SendWithRefreshRetryAsync(client, baseUrl, c => c.GetAsync(BuildTurnsUrl(baseUrl, arguments))),
                _                        => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized) {
                return BuildToolResult(id, NotLoggedInMessage, isError: true);
            }

            if (!httpResponse.IsSuccessStatusCode) {
                return BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true);
            }

            // Client-side projection for get_session_summary: project /recap entries into { summary_text, plan }.
            var payload = toolName == "get_session_summary" ? ProjectRecapToSummary(body) : body;

            return BuildToolResult(id, payload);
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
    static async Task<HttpResponseMessage> SendWithRefreshRetryAsync(HttpClient client, string baseUrl, Func<HttpClient, Task<HttpResponseMessage>> send) {
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
            ? (await TokenStore.GetValidTokensForServerAsync(baseUrl)).Tokens
            : await TokenStore.RecoverForServerAsync(baseUrl, rejected);

        if (refreshed is null) return response; // genuinely not logged in; keep the original 401

        response.Dispose();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        return await send(client);
    }

    internal static string BuildSearchUrl(string baseUrl, JsonObject? args, string? cwdRepoHash) {
        var url = $"{baseUrl}/api/sessions/search";
        var qs  = new List<string>();

        if (args?["query"]?.GetValue<string>() is { Length: > 0 } q) {
            qs.Add($"q={Uri.EscapeDataString(q)}");
        }

        if (args?["author"]?.GetValue<string>() is { Length: > 0 } author) {
            qs.Add($"author={Uri.EscapeDataString(author)}");
        }

        if (TryReadLong(args, "author_github_id", out var aid)) {
            qs.Add($"author_github_id={aid}");
        }

        // repo scope: explicit "all" → cross-repo; explicit value → that repo; else cwd repo.
        // Fail closed rather than silently broadening: if we can't resolve the current repo
        // and the caller didn't explicitly opt into cross-repo, error instead of searching everything.
        // Read `repo` defensively: a non-string JSON value must yield a clean validation error
        // (caught as a tool error), not an unhandled InvalidOperationException that the outer
        // guard turns into a generic "internal error".
        string? explicitRepo = args?["repo"] switch {
            null                                          => null,
            JsonValue v when v.TryGetValue(out string? s) => s,
            _                                             => throw new ArgumentException(
                "`repo` must be a string — \"<owner>/<name>\", a 16-hex repo hash, or \"all\".")
        };
        if (string.IsNullOrWhiteSpace(explicitRepo)) explicitRepo = null;

        if (string.Equals(explicitRepo, "all", StringComparison.OrdinalIgnoreCase)) {
            // cross-repo: omit the repo filter entirely.
        } else {
            var repo = explicitRepo ?? cwdRepoHash;
            if (repo is null)
                throw new ArgumentException(
                    "Cannot resolve the current repository owner/name from git metadata (e.g. a missing or " +
                    "unparseable 'origin' remote). Pass repo: \"<owner>/<name>\" for a specific repo, or " +
                    "repo: \"all\" to search across all visible repos.");
            qs.Add($"repo={Uri.EscapeDataString(repo)}");
        }

        if (TryReadInt(args, "limit", out var limit)) {
            qs.Add($"limit={limit}");
        }

        if (TryReadInt(args, "offset", out var offset)) {
            qs.Add($"offset={offset}");
        }

        return qs.Count == 0 ? url : url + "?" + string.Join("&", qs);
    }

    /// <summary>
    /// AI-1769 auto-widen, decision half: the implicit cwd-repo pin is the #1 cause
    /// of "agent can't find it, human can". Widen ONLY when the pin was implicit
    /// (no explicit repo arg), a cwd repo actually resolved, the caller isn't
    /// paginating, the response isn't an author short-circuit (disambiguation /
    /// no-match — widening can't fix those), and the pinned search came back thin.
    /// </summary>
    internal static bool ShouldWiden(JsonObject? args, string? cwdRepoHash, string firstBody, out int limit) {
        limit = 10;
        if (TryReadInt(args, "limit", out var requested)) limit = requested;

        string? explicitRepo = args?["repo"] switch {
            null                                          => null,
            JsonValue v when v.TryGetValue(out string? s) => s,
            _                                             => null
        };

        if (!string.IsNullOrWhiteSpace(explicitRepo)) return false;
        if (cwdRepoHash is null) return false;
        if (TryReadInt(args, "offset", out var offset) && offset > 0) return false;

        try {
            if (JsonNode.Parse(firstBody) is not JsonObject root) return false;
            if (root["disambiguation"] is JsonArray { Count: > 0 }) return false;
            if (root["no_author_match"]?.GetValue<bool>() is true) return false;
            if (root["too_many_author_matches"]?.GetValue<bool>() is true) return false;

            var hits = root["hits"] as JsonArray;

            return (hits?.Count ?? 0) < limit;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// AI-1769 auto-widen, merge half: cwd-repo hits first, widened hits appended
    /// (deduped by session_id), capped at the requested limit, with a top-level
    /// widened_to_all_repos marker so the agent knows the scope grew. Falls back to
    /// the first body untouched on any parse failure — widening is best-effort and
    /// must never cost the caller a successful result.
    /// </summary>
    internal static string MergeWidenedBody(string firstBody, string widenedBody, int limit) {
        try {
            if (JsonNode.Parse(firstBody) is not JsonObject first) return firstBody;
            if (JsonNode.Parse(widenedBody) is not JsonObject widened) return firstBody;

            var firstHits   = first["hits"] as JsonArray ?? [];
            var widenedHits = widened["hits"] as JsonArray ?? [];
            var seen        = new HashSet<string>(StringComparer.Ordinal);
            var merged      = new JsonArray();

            foreach (var hit in firstHits) {
                if (merged.Count >= limit) break;
                if (hit?["session_id"]?.GetValue<string>() is not { } sid || !seen.Add(sid)) continue;
                merged.Add(hit.DeepClone());
            }

            foreach (var hit in widenedHits) {
                if (merged.Count >= limit) break;
                if (hit?["session_id"]?.GetValue<string>() is not { } sid || !seen.Add(sid)) continue;
                merged.Add(hit.DeepClone());
            }

            first["hits"]                 = merged;
            first["widened_to_all_repos"] = true;

            return first.ToJsonString();
        } catch {
            return firstBody;
        }
    }

    static string BuildSummaryUrl(string baseUrl, JsonObject? args) {
        var id = args?["session_id"]?.GetValue<string>()
         ?? throw new ArgumentException("Missing required argument: session_id");

        return $"{baseUrl}/api/sessions/{Uri.EscapeDataString(id)}/recap?chain=false";
    }

    static string BuildTurnDetailUrl(string baseUrl, JsonObject? args) {
        var id = args?["session_id"]?.GetValue<string>()
         ?? throw new ArgumentException("Missing required argument: session_id");

        if (!TryReadInt(args, "turn_index", out var turnIndex)) {
            throw new ArgumentException("Missing required argument: turn_index");
        }

        return $"{baseUrl}/api/sessions/{Uri.EscapeDataString(id)}/turns/{turnIndex}";
    }

    internal static string BuildTurnsUrl(string baseUrl, JsonObject? args) {
        var id = args?["session_id"]?.GetValue<string>()
         ?? throw new ArgumentException("Missing required argument: session_id");

        return $"{baseUrl}/api/sessions/{Uri.EscapeDataString(id)}/turns";
    }

    static string BuildTranscriptUrl(string baseUrl, JsonObject? args) {
        var id = args?["session_id"]?.GetValue<string>()
         ?? throw new ArgumentException("Missing required argument: session_id");

        var url = $"{baseUrl}/api/sessions/{Uri.EscapeDataString(id)}/transcript";
        var qs  = new List<string>();

        if (TryReadInt(args, "around_event", out var a)) {
            qs.Add($"around_event={a}");
        }

        if (args?["agent_id"]?.GetValue<string>() is { Length: > 0 } aid) {
            qs.Add($"agent_id={Uri.EscapeDataString(aid)}");
        }

        if (TryReadInt(args, "before", out var b)) {
            qs.Add($"before={b}");
        }

        if (TryReadInt(args, "after", out var af)) {
            qs.Add($"after={af}");
        }

        if (TryReadInt(args, "limit", out var l)) {
            qs.Add($"limit={l}");
        }

        if (TryReadInt(args, "offset", out var o)) {
            qs.Add($"offset={o}");
        }

        if (TryReadBool(args, "chain", out var c)) {
            qs.Add($"chain={(c ? "true" : "false")}");
        }

        if (TryReadBool(args, "include_thinking", out var t)) {
            qs.Add($"include_thinking={(t ? "true" : "false")}");
        }

        return qs.Count == 0 ? url : url + "?" + string.Join("&", qs);
    }

    /// <summary>
    /// Reads a numeric field as int, tolerant of JsonValue holding any underlying numeric type
    /// (int/long/double) — TryGetValue&lt;int&gt; on a JsonValue constructed from a long returns false.
    /// Returns false when the key is missing or the value is the wrong shape (e.g., a string).
    /// Throws <see cref="ArgumentException"/> when the value is numeric but out of range for int,
    /// so the caller (via <see cref="HandleToolCallAsync"/>) surfaces it as a JSON-RPC validation error
    /// rather than silently falling back to the default.
    /// </summary>
    internal static bool TryReadInt(JsonObject? args, string key, out int value) {
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

    /// <summary>
    /// Reads a numeric field as long, tolerant of JsonValue holding int/long/double.
    /// Throws <see cref="ArgumentException"/> when a double value is out of range for long
    /// or has a non-integer fractional part.
    /// </summary>
    internal static bool TryReadLong(JsonObject? args, string key, out long value) {
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

        if (v.TryGetValue<int>(out var iv)) {
            value = iv;

            return true;
        }

        if (v.TryGetValue<double>(out var dv)) {
            // long.MaxValue (9.22e18) is not exactly representable as double; the smallest double
            // strictly greater than long.MaxValue is 9223372036854775808.0. Comparing against that
            // boundary avoids the (long)dv cast overflowing silently.
            if (dv < long.MinValue || dv >= 9223372036854775808.0 || Math.Truncate(dv) != dv)
                throw new ArgumentException($"'{key}' value {dv} is out of range or non-integer for long.");

            value = (long)dv;

            return true;
        }

        return false;
    }

    static bool TryReadBool(JsonObject? args, string key, out bool value) {
        value = false;
        var node = args?[key];

        if (node is null) return false;

        try {
            return node.AsValue().TryGetValue(out value);
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Projects a /recap response (RecapEntry[]) into { summary_text, plan } for agent consumption.
    /// "Latest of type wins" — walks entries in order and keeps the last value for each type.
    /// </summary>
    internal static string ProjectRecapToSummary(string body) {
        string? summaryText = null;
        string? plan        = null;

        try {
            if (JsonNode.Parse(body) is JsonArray root) {
                foreach (var node in root) {
                    var type    = node?["type"]?.GetValue<string>();
                    var content = node?["content"]?.GetValue<string>();

                    if (content is null) continue;

                    switch (type) {
                        case "whats_done":
                            summaryText = content; break;
                        case "plan":
                            plan = content; break;
                    }
                }
            }
        } catch {
            // Malformed body — fall through with empty projection.
        }

        // AOT-safe construction: build a JSON fragment from encoded primitives rather than assigning
        // strings directly to a JsonObject. JsonNode.Parse handles arbitrary string content safely.
        var sb = new StringBuilder();
        sb.Append("{\"summary_text\":");
        AppendJsonString(sb, summaryText ?? "");
        sb.Append(",\"plan\":");

        if (plan is null) {
            sb.Append("null");
        } else {
            AppendJsonString(sb, plan);
        }

        sb.Append('}');

        return sb.ToString();
    }

    static void AppendJsonString(StringBuilder sb, string value) {
        sb.Append('"');
        sb.Append(JsonEncodedText.Encode(value));
        sb.Append('"');
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

    static McpTool[] BuildToolsList() => [
        new(
            "search_sessions",
            "Search past Kurrent Capacitor sessions in the current repo (or across all visible repos with repo: \"all\") by free-text question and/or author name. Returns ranked hits with session_id, title, owner, snippet, and (for transcript hits) hit_event_index + agent_id for drilling into the exact moment with get_session_transcript. For 'have we done this before / why did we / who decided X / when did we work on Y' questions, search here before grepping the code or git log — it searches the reasoning across past sessions, not just the code.",
            new(
                "object",
                new() {
                    ["query"] = new("string", "Free-text FTS query. Empty allowed when author is set."),
                    ["author"] = new("string", "Optional: GitHub username or display name. Fuzzy match."),
                    ["author_github_id"] = new("integer", "Optional: explicit GitHub numeric id. Takes precedence over `author`."),
                    ["repo"] = new("string", "Optional: \"all\" for cross-repo, \"<owner>/<name>\", or a 16-hex repo hash. Defaults to the current repo (resolved from cwd at server startup)."),
                    ["limit"] = new("integer", "Default 10, max 50."),
                    ["offset"] = new("integer", "Default 0, max 500.")
                },
                []
            )
        ),
        new(
            "get_session_summary",
            "Get a concise summary of a past session: the 'what was done' narrative (summary_text) and the plan (if any). Use this to orient yourself before drilling into the full transcript.",
            new(
                "object",
                new() { ["session_id"] = new("string", "Session ID returned by search_sessions") },
                ["session_id"]
            )
        ),
        new(
            "get_session_transcript",
            "Get speaker-tagged transcript excerpts from a past session. Use `around_event` (paired with `agent_id` for subagent hits) returned by search_sessions to fetch the exact decision context. Default window is 50 events from the beginning; with `around_event` it's ±5/15 by default.",
            new(
                "object",
                new() {
                    ["session_id"]       = new("string", "Session ID."),
                    ["around_event"]     = new("integer", "Center the window around this event index."),
                    ["agent_id"]         = new("string", "When the search hit was in a subagent stream, the agent_id returned alongside hit_event_index."),
                    ["before"]           = new("integer", "Events before around_event. Default 5."),
                    ["after"]            = new("integer", "Events after around_event. Default 15."),
                    ["limit"]            = new("integer", "When around_event is unset. Default 50."),
                    ["offset"]           = new("integer", "When around_event is unset. Default 0."),
                    ["chain"]            = new("boolean", "Include chained_sessions metadata. Default false."),
                    ["include_thinking"] = new("boolean", "Include assistant thinking blocks. Default false.")
                },
                ["session_id"]
            )
        ),
        new(
            "get_turn",
            "Get the full event transcript for one turn (user prompt, tool calls + results, assistant text) by session_id + turn_index. A turn is one user message and the assistant's full response up to the next user message. Use get_session_summary or search_sessions to find a session, then drill into specific turns by index.",
            new(
                "object",
                new() {
                    ["session_id"] = new("string",  "Session ID (from search_sessions or get_session_summary)."),
                    ["turn_index"] = new("integer", "Zero-based turn index.")
                },
                ["session_id", "turn_index"]
            )
        ),
        new(
            "list_turns",
            "List all turns of a past session with their prose summaries. A turn is one user message and the assistant's full response up to the next user message. Returns per turn: turn_index, prose (1-3 sentence summary; may be null for trivial/older turns), user_prompt, tools, files, and token counts. Use this to map a session turn by turn, then call get_turn(session_id, turn_index) for one turn's full transcript, or get_session_summary for the whole-session narrative.",
            new(
                "object",
                new() { ["session_id"] = new("string", "Session ID (from search_sessions or get_session_summary).") },
                ["session_id"]
            )
        )
    ];
}
