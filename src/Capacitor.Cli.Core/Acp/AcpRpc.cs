using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Acp;

/// <summary>
/// JSON-RPC 2.0 request frame for the ACP stdio transport (client → agent, or agent → client for
/// server-initiated requests like <c>session/request_permission</c> / <c>fs/*</c>). <see cref="Params"/>
/// stays a <see cref="JsonElement"/> so this layer is non-polymorphic and AOT/trim-safe — the
/// connection layer parses it into a typed shape once the method is known. Wire
/// shape confirmed by the probe in <c>docs/acp-probe-findings.md</c>:
/// <c>{"jsonrpc":"2.0","id":1,"method":"initialize","params":{...}}</c>.
/// </summary>
public sealed record AcpRequest(
    [property: JsonPropertyName("id")]     long         Id,
    [property: JsonPropertyName("method")] string       Method,
    [property: JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                            JsonElement? Params
) {
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc => "2.0";
}

/// <summary>
/// JSON-RPC 2.0 notification frame for the ACP stdio transport — a request with no <c>id</c>, so no
/// response is expected. Used for both directions observed in the probe: agent → client
/// (<c>session/update</c>) and client → agent (<c>session/cancel</c>).
/// </summary>
public sealed record AcpNotification(
    [property: JsonPropertyName("method")] string       Method,
    [property: JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                            JsonElement? Params
) {
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc => "2.0";
}

/// <summary>
/// JSON-RPC 2.0 response frame. Carries EITHER <see cref="Result"/> OR <see cref="Error"/>, never
/// both — both are omitted from the JSON when null so a success response has no <c>error</c> key and
/// an error response has no <c>result</c> key, matching the probe-captured wire shapes.
/// </summary>
public sealed record AcpResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                        JsonElement? Result,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                        AcpError?    Error
) {
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc => "2.0";
}

/// <summary>
/// JSON-RPC 2.0 error object embedded in an <see cref="AcpResponse"/>. <see cref="Data"/> stays a
/// <see cref="JsonElement"/> since its shape is error-specific and untyped on the wire (the probe
/// observed an array of Zod validation issues for one internal-error case).
/// </summary>
public sealed record AcpError(
    [property: JsonPropertyName("code")]    int          Code,
    [property: JsonPropertyName("message")] string       Message,
    [property: JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                             JsonElement? Data
);
