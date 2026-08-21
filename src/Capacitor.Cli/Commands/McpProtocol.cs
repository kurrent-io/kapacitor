using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Shared MCP protocol plumbing so every kcap MCP server answers the standard capability
/// probes (<c>resources/list</c>, <c>prompts/list</c>, <c>ping</c>) and negotiates the
/// protocol version — instead of returning <c>-32601</c>, which some clients treat as fatal
/// and drop the server. JsonNode DOM only → AOT-safe.
/// </summary>
static class McpProtocol {
    /// The version we advertise when we don't recognise the client's request.
    const string BaselineVersion = "2024-11-05";

    /// Protocol versions this server is compatible with. We echo the client's requested
    /// version when it's one of these, so newer clients negotiate up cleanly.
    static readonly HashSet<string> Supported = ["2024-11-05", "2025-03-26", "2025-06-18"];

    /// <summary>The protocolVersion to advertise: the client's requested version if we
    /// support it, else our baseline. <paramref name="initializeRequest"/> is the full
    /// JSON-RPC initialize request object.</summary>
    public static string NegotiateVersion(JsonObject initializeRequest) {
        // Read params.protocolVersion DEFENSIVELY — a malformed initialize (non-object params, or a
        // non-string protocolVersion) must fall back to baseline, never throw. The initialize dispatch
        // arm has no try/catch, so a throw here would kill the stdio server.
        var requested = (initializeRequest["params"] as JsonObject)?["protocolVersion"] is JsonValue v
                        && v.TryGetValue(out string? s) ? s : null;
        return requested is not null && Supported.Contains(requested) ? requested : BaselineVersion;
    }

    /// <summary>Answers the standard capability-probe methods a client may send regardless of
    /// advertised capabilities. Returns a full JSON-RPC response string, or null if
    /// <paramref name="method"/> isn't one of them (caller falls back to -32601).</summary>
    public static string? TryHandleStandardMethod(string? method, JsonNode id) => method switch {
        "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
        "prompts/list"   => Result(id, new JsonObject { ["prompts"]   = new JsonArray() }),
        "ping"           => Result(id, new JsonObject()),
        _                => null
    };

    static string Result(JsonNode id, JsonObject result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString();
}
