using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

static class McpMemoryServer {
    internal const string NotLoggedInMessage = "Not logged in. Run 'kcap login' on the host shell.";

    public static async Task<int> RunAsync(string baseUrl) {
        var cwdRepoHash = await ResolveCwdRepoHashAsync();
        var machineId   = await ResolveMachineIdAsync();
        var tools       = BuildToolsList();

        // Validate the server_url shape once, locally (pure string check — no network, token,
        // or stderr). Used to fail gracefully instead of hard-exiting mid-request (below).
        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // The authenticated client is created on the first tools/call, not at startup:
        // kcap-memory auto-registers, so Claude Code spawns `kcap mcp memory` for every
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
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, cwdRepoHash, machineId);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp memory: unexpected error handling tools/call: {ex}");
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

    static async Task<string?> ResolveMachineIdAsync() {
        try { return await MachineIdProvider.GetOrCreateAsync(); } catch { return null; }
    }

    // Server-level usage preamble (MCP `instructions`) — steers clients to check for prior art here
    // before assuming none, and to save durable learnings.
    const string ServerInstructions =
        "Use these tools for durable team knowledge — preferences, feedback, project facts, references. " +
        "Search memories before assuming there's no prior art on a task, and before saving a new one — they " +
        "hold learnings that aren't in the code.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-memory", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    internal static async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl,
            string?    cwdRepoHash,
            string?    machineId
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            using var httpResponse = toolName switch {
                "search_memories" => await SendWithRefreshRetryAsync(client, c => c.GetAsync(BuildSearchUrl(baseUrl, arguments, cwdRepoHash, machineId))),
                "get_memory"      => await SendWithRefreshRetryAsync(client, c => c.GetAsync(BuildGetUrl(baseUrl, arguments, cwdRepoHash, machineId))),
                "save_memory"     => await SendWithRefreshRetryAsync(client, c => c.PostAsync($"{baseUrl}/api/memories", ToJsonContent(BuildSaveBody(arguments, cwdRepoHash, machineId)))),
                "update_memory"   => await SendWithRefreshRetryAsync(client, c => c.PutAsync($"{baseUrl}/api/memories/{Uri.EscapeDataString(Id(arguments))}", ToJsonContent(BuildUpdateBody(arguments)))),
                "rescope_memory"  => await SendWithRefreshRetryAsync(client, c => c.PostAsync($"{baseUrl}/api/memories/{Uri.EscapeDataString(Id(arguments))}/rescope", ToJsonContent(BuildRescopeBody(arguments)))),
                "archive_memory"  => await SendWithRefreshRetryAsync(client, c => c.DeleteAsync($"{baseUrl}/api/memories/{Uri.EscapeDataString(Id(arguments))}")),
                _                 => throw new ArgumentException($"Unknown tool: {toolName}")
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

    static string Id(JsonObject? args) =>
        args?["id"]?.GetValue<string>() is { Length: > 0 } id ? id : throw new ArgumentException("id is required");

    internal static string BuildSearchUrl(string baseUrl, JsonObject? args, string? cwdRepoHash, string? machineId) {
        var q = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(q)) throw new ArgumentException("query is required");

        var qs = new List<string> { $"q={Uri.EscapeDataString(q)}" };
        if (cwdRepoHash is not null) qs.Add($"repo={Uri.EscapeDataString(cwdRepoHash)}");
        if (machineId is not null) qs.Add($"machine={Uri.EscapeDataString(machineId)}");
        if (TryReadInt(args, "limit", out var limit)) qs.Add($"limit={limit}");

        return $"{baseUrl}/api/memories/search?{string.Join("&", qs)}";
    }

    internal static string BuildGetUrl(string baseUrl, JsonObject? args, string? cwdRepoHash, string? machineId) {
        var idOrSlug = args?["id_or_slug"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(idOrSlug)) throw new ArgumentException("id_or_slug is required");

        var qs = new List<string>();
        if (cwdRepoHash is not null) qs.Add($"repo={Uri.EscapeDataString(cwdRepoHash)}");
        if (machineId is not null) qs.Add($"machine={Uri.EscapeDataString(machineId)}");

        var url = $"{baseUrl}/api/memories/{Uri.EscapeDataString(idOrSlug)}";
        return qs.Count == 0 ? url : $"{url}?{string.Join("&", qs)}";
    }

    // NOTE: request bodies use snake_case keys (repo_hash, machine_tag, source_session_id) —
    // the server's global JSON policy is JsonNamingPolicy.SnakeCaseLower.
    // Responses are passed through as raw text, so only these request-body builders are affected.
    internal static JsonObject BuildSaveBody(JsonObject? args, string? cwdRepoHash, string? machineId) {
        string Req(string name) =>
            args?[name]?.GetValue<string>() is { Length: > 0 } v ? v : throw new ArgumentException($"{name} is required");

        var global          = args?["global"]?.GetValue<bool>() == true;
        var machineSpecific = args?["machine_specific"]?.GetValue<bool>() == true;

        // Fail closed rather than silently broadening scope: a null cwdRepoHash with global not
        // explicitly requested would otherwise be sent to the server as repo_hash: null, which the
        // server treats as a GLOBAL (all-repos) memory. Likewise a null machineId with
        // machine_specific: true would otherwise save an untagged (visible-to-everyone) memory.
        if (!global && cwdRepoHash is null)
            throw new ArgumentException("Cannot resolve the current repository — run from a git checkout or pass global: true for a repo-independent memory.");

        if (machineSpecific && machineId is null)
            throw new ArgumentException("Machine id unavailable — cannot save a machine-specific memory on this host.");

        return new JsonObject {
            ["audience"]          = Req("audience"),
            ["slug"]              = Req("slug"),
            ["description"]       = Req("description"),
            ["content"]           = Req("content"),
            ["kind"]              = Req("kind"),
            ["team"]              = args?["team"]?.GetValue<string>(),
            ["repo_hash"]         = global ? null : cwdRepoHash,
            ["machine_tag"]       = machineSpecific ? machineId : null,
            ["machine_context"]   = machineId,
            ["source_session_id"] = null,
            ["harness"]           = "mcp"
        };
    }

    internal static JsonObject BuildUpdateBody(JsonObject? args) => new() {
        ["description"] = args?["description"]?.GetValue<string>(),
        ["content"]     = args?["content"]?.GetValue<string>(),
        ["kind"]        = args?["kind"]?.GetValue<string>()
    };

    internal static JsonObject BuildRescopeBody(JsonObject? args) => new() {
        ["audience"] = args?["audience"]?.GetValue<string>() is { Length: > 0 } a ? a : throw new ArgumentException("audience is required"),
        ["team"]     = args?["team"]?.GetValue<string>()
    };

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
        new("search_memories",
            "Search the team's shared memories (hybrid semantic + keyword). Call this before saving a new memory, and before assuming there's no prior art on a task — it surfaces durable team learnings (preferences, feedback, project facts) that aren't in the code.",
            new("object", new() {
                ["query"] = new("string", "What to search for."),
                ["limit"] = new("number", "Max results (default 10, max 50).")
            }, ["query"])),
        new("get_memory",
            "Fetch a memory's full content by id or slug. Slug resolution precedence: your memories, then your teams', then org-wide; repo-scoped before global.",
            new("object", new() {
                ["id_or_slug"] = new("string", "Memory id (32 hex) or slug.")
            }, ["id_or_slug"])),
        new("save_memory",
            "Save a durable learning to the server. audience: 'user' (private), 'team', or 'org' (everyone). Saves are repo-scoped by default (to the cwd's git checkout); if the current repo can't be resolved, pass global: true for a repo-independent memory, or the save fails. Prefer update_memory when the result reports a nearDuplicate.",
            new("object", new() {
                ["audience"]         = new("string", "user | team | org"),
                ["slug"]             = new("string", "kebab-case identifier, unique within the audience+repo pool"),
                ["description"]      = new("string", "One-line summary (max 300 chars)"),
                ["content"]          = new("string", "Full memory body (max 64 KiB)"),
                ["kind"]             = new("string", "preference | feedback | project | reference"),
                ["team"]             = new("string", "Team name or id — required for audience 'team' if you are in several teams"),
                ["global"]           = new("boolean", "true = not tied to the current repo (required if not run from a git checkout; default: scoped to cwd repo)"),
                ["machine_specific"] = new("boolean", "true = only relevant on this machine (user audience only)")
            }, ["audience", "slug", "description", "content", "kind"])),
        new("update_memory",
            "Update an existing memory's description/content/kind (any subset).",
            new("object", new() {
                ["id"]          = new("string", "Memory id"),
                ["description"] = new("string", "New one-line summary"),
                ["content"]     = new("string", "New body"),
                ["kind"]        = new("string", "preference | feedback | project | reference")
            }, ["id"])),
        new("rescope_memory",
            "Change a memory's audience (e.g. promote your user memory to team or org).",
            new("object", new() {
                ["id"]       = new("string", "Memory id"),
                ["audience"] = new("string", "user | team | org"),
                ["team"]     = new("string", "Target team when audience is 'team'")
            }, ["id", "audience"])),
        new("archive_memory",
            "Archive (soft-delete) a memory.",
            new("object", new() { ["id"] = new("string", "Memory id") }, ["id"]))
    ];
}
