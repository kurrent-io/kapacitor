// src/Capacitor.Cli.Core/Acp/AcpMessages.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Acp;

/// <summary>
/// Typed <c>params</c> payloads for the ACP methods <see cref="Daemon.Acp.AcpConnection"/> callers
/// (<c>AcpHostedAgentRuntime</c>, Task 9) send. These exist only so request construction can
/// go through source-gen (<see cref="JsonSerializer.SerializeToElement{T}(T, System.Text.Json.Serialization.Metadata.JsonTypeInfo{T})"/>
/// against <see cref="CapacitorJsonContext"/>) instead of the reflection-based overloads, which are
/// unsafe under NativeAOT. Field names/shapes are pinned to the probe-confirmed wire shapes in
/// <c>docs/acp-probe-findings.md</c> — every property carries an explicit
/// <see cref="JsonPropertyNameAttribute"/> because the wire protocol uses camelCase while this
/// context's default naming policy (set on <see cref="CapacitorJsonContext"/>) is snake_case.
/// </summary>

/// <summary>
/// <c>initialize</c> params. Deliberately advertises MINIMAL client capabilities (no <c>fs</c>, no
/// <c>terminal</c>) — those get decided later based on ACP probe findings; this type implements
/// neither capability. Since the multi-select end-to-end shipped (form-mode elicitation lane in
/// this daemon + the server/UI half in kcap-server), <see cref="ClientCapabilities.Elicitation"/>
/// advertises FORM-mode elicitation support.
/// </summary>
public sealed record InitializeParams(
    [property: JsonPropertyName("protocolVersion")]  int                     ProtocolVersion,
    [property: JsonPropertyName("clientCapabilities")] ClientCapabilities    ClientCapabilities
);

public sealed record ClientCapabilities(
    [property: JsonPropertyName("fs")]       FsCapabilities Fs,
    [property: JsonPropertyName("terminal")] bool           Terminal,
    // Trailing + WhenWritingNull so every pre-existing 2-arg construction (and its wire shape)
    // stays byte-for-byte unchanged; the runtime's initialize call sites opt in explicitly.
    [property: JsonPropertyName("elicitation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                             ElicitationCapabilities? Elicitation = null
);

/// <summary>
/// Client elicitation capability advertisement (marked UNSTABLE in the SDK schema — pinned by
/// the fixture generator's drift contract, see <c>test-fixtures/acp-elicitation/generate.mjs</c>).
/// FORM mode only: supplying <c>{}</c> for <see cref="Form"/> means "form-based elicitation
/// supported" per the schema. <c>url</c> is DELIBERATELY not modeled — omission is the spec's
/// "unsupported" signal, and this daemon cancels url-mode frames
/// (<c>AcpInteractionBridge</c>'s mode gate) rather than opening arbitrary URLs on the host.
/// </summary>
public sealed record ElicitationCapabilities(
    [property: JsonPropertyName("form")] ElicitationFormCapabilities Form
);

/// <summary>Serializes as the bare <c>{}</c> the schema requires for "supported".</summary>
public sealed record ElicitationFormCapabilities;

/// <summary>
/// <c>initialize</c> result — <c>AcpHostedAgentRuntime.StartAsync</c> deserializes the agent's
/// <c>initialize</c> response into this to validate <see cref="ProtocolVersion"/> (must be <c>1</c>;
/// the daemon speaks no other version yet) and to capture <see cref="AgentCapabilities"/> for later
/// features (e.g. a reconnect path gated on <see cref="Acp.AgentCapabilities.LoadSession"/>).
/// Deliberately minimal — the real wire response also carries <c>promptCapabilities</c>/
/// <c>authMethods</c>, neither of which the daemon needs yet.
/// </summary>
public sealed record InitializeResult(
    [property: JsonPropertyName("protocolVersion")]  int                 ProtocolVersion,
    [property: JsonPropertyName("agentCapabilities")] AgentCapabilities?  AgentCapabilities
);

/// <summary>
/// Agent-advertised capabilities from <c>initialize</c>'s result — only <see cref="LoadSession"/> is
/// modeled for now (captured for a future reconnect path; nothing acts on it yet).
/// <see cref="LoadSession"/> defaults to <see langword="false"/> when the wire omits it,
/// matching the ACP spec's "absent means unsupported" convention.
/// </summary>
public sealed record AgentCapabilities(
    [property: JsonPropertyName("loadSession")] bool LoadSession = false
);

public sealed record FsCapabilities(
    [property: JsonPropertyName("readTextFile")]  bool ReadTextFile,
    [property: JsonPropertyName("writeTextFile")] bool WriteTextFile
);

/// <summary>One MCP server <c>session/new</c> can hand the agent — stdio transport only (no
/// caller needs http/sse yet; add a discriminated Transport field if/when one does).
/// <see cref="Args"/> and <see cref="Env"/> are always sent as arrays on the wire, never
/// <see langword="null"/> — the constructor normalizes a <see langword="null"/> input to <c>[]</c>
/// and defensively clones a non-null input so a caller can't mutate this record's serialized
/// payload after construction.</summary>
public sealed record AcpMcpServerSpec {
    [JsonPropertyName("name")]    public string               Name    { get; }
    [JsonPropertyName("command")] public string               Command { get; }
    [JsonPropertyName("args")]    public string[]             Args    { get; }
    [JsonPropertyName("env")]     public AcpMcpServerEnvVar[] Env     { get; }

    public AcpMcpServerSpec(string Name, string Command, string[]? Args, AcpMcpServerEnvVar[]? Env) {
        this.Name    = Name;
        this.Command = Command;
        this.Args    = Args is null ? [] : [.. Args];
        this.Env     = Env  is null ? [] : [.. Env];
    }
}

public sealed record AcpMcpServerEnvVar(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("value")] string Value
);

/// <summary><c>session/new</c> params. <c>Cwd</c> must be an absolute path (the worktree root).
/// <c>McpServers</c> is always sent as an array (never omitted) — empty for every launch until a
/// caller populates RuntimeStartContext.McpServers (no caller does yet — the reviewer path is
/// the first planned consumer).</summary>
public sealed record SessionNewParams(
    [property: JsonPropertyName("cwd")]        string             Cwd,
    [property: JsonPropertyName("mcpServers")] AcpMcpServerSpec[] McpServers
);

/// <summary><c>session/load</c> params — protocol-native resume of a prior session on a freshly
/// spawned agent process (same <c>sessionId</c>, same absolute <c>cwd</c>, and the SAME
/// <c>mcpServers</c> list the original launch carried). The agent replays the session's history as
/// <c>session/update</c> notifications and, per the ACP spec's MUST, responds only after all
/// conversation entries have streamed — the response is the reconnect path's closed-world
/// end-of-replay barrier (probe-verified for Cursor and Copilot,
/// <c>docs/probes/2026-08-04-acp-reconnect-c0/</c>).</summary>
public sealed record SessionLoadParams(
    [property: JsonPropertyName("sessionId")]  string             SessionId,
    [property: JsonPropertyName("cwd")]        string             Cwd,
    [property: JsonPropertyName("mcpServers")] AcpMcpServerSpec[] McpServers
);

/// <summary><c>session/prompt</c> params — a content-block array, per the probe (not a bare string).</summary>
public sealed record SessionPromptParams(
    [property: JsonPropertyName("sessionId")] string             SessionId,
    [property: JsonPropertyName("prompt")]    PromptContentBlock[] Prompt
);

public sealed record PromptContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text
);

/// <summary><c>session/cancel</c> params — sent as a notification (no response expected).</summary>
public sealed record SessionCancelParams(
    [property: JsonPropertyName("sessionId")] string SessionId
);

/// <summary>
/// <c>session/set_config_option</c> params (model selection). Sent AFTER
/// <c>session/new</c> resolves and BEFORE the first <c>session/prompt</c>, with the response
/// awaited so the model is set before the turn starts. Wire shape probe-confirmed against real
/// <c>cursor-agent</c> (<c>docs/ai-688-cursor-prototype-findings.md</c>): <see cref="ConfigId"/> is
/// the literal <c>"model"</c> (the field is named <c>configId</c> on the wire, NOT <c>id</c> — an
/// earlier attempt using <c>id</c> got a Zod <c>invalid_type</c> error at path <c>configId</c>), and
/// <see cref="Value"/> must be the EXACT, parameterized <c>modelId</c> from
/// <see cref="SessionModelsInfo.AvailableModels"/> (e.g.
/// <c>claude-sonnet-4-5[thinking=true,context=200k]</c>) — a bare family name is not a valid wire
/// value. See <c>Capacitor.Cli.Core.Acp.AcpModelResolver</c> for how a requested family name/exact
/// id is resolved to this exact value.
/// </summary>
public sealed record SetConfigOptionParams(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("configId")]  string ConfigId,
    [property: JsonPropertyName("value")]     string Value
);

/// <summary>
/// <c>session/set_model</c> params — the stabilized ACP model-selection method, used by vendors
/// that do not implement <c>session/set_config_option</c>. Sent at the same point in the handshake
/// as <see cref="SetConfigOptionParams"/> (after <c>session/new</c>, before the first
/// <c>session/prompt</c>, response awaited). Wire shape probe-confirmed against real
/// <c>kiro-cli</c> 2.16.0 (<c>docs/probes/2026-08-05-kiro-model-override/</c>):
/// <see cref="ModelId"/> is an exact id from <see cref="SessionModelsInfo.AvailableModels"/>
/// (Kiro's are bare, e.g. <c>deepseek-3.2</c> — resolved by
/// <c>Capacitor.Cli.Core.Acp.AcpModelResolver</c> like Cursor's), and the success response is an
/// empty object.
/// </summary>
public sealed record SetModelParams(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("modelId")]   string ModelId
);

/// <summary>
/// Typed shape for <c>session/new</c>'s <c>result.models</c> object — the daemon
/// otherwise treats the <c>session/new</c> result as an opaque <see cref="JsonElement"/> (only
/// <c>sessionId</c> is read out of it today); this record exists purely so
/// <c>Capacitor.Cli.Core.Acp.AcpModelResolver</c> can resolve a requested model against
/// <see cref="AvailableModels"/> without ad hoc <see cref="JsonElement"/> digging. Probe-confirmed
/// shape: <c>{"currentModelId":"...","availableModels":[{"modelId":"...","name":"..."}]}</c>.
/// </summary>
public sealed record SessionModelsInfo(
    [property: JsonPropertyName("currentModelId")]  string?              CurrentModelId,
    [property: JsonPropertyName("availableModels")] AvailableModelDto[]? AvailableModels
);

/// <summary>
/// One entry in <see cref="SessionModelsInfo.AvailableModels"/>. <see cref="ModelId"/> is the
/// exact, parameterized wire value <c>session/set_config_option</c>'s <c>value</c> requires (e.g.
/// <c>claude-sonnet-4-5[thinking=true,context=200k]</c>); <see cref="Name"/> is the shorter
/// human-readable family name (e.g. <c>claude-sonnet-4-5</c>) a caller is more likely to request.
/// </summary>
public sealed record AvailableModelDto(
    [property: JsonPropertyName("modelId")] string  ModelId,
    [property: JsonPropertyName("name")]    string? Name
);

/// <summary>
/// <c>session/request_permission</c> params sent BY THE AGENT (server-initiated request, handled
/// via <see cref="Daemon.Acp.AcpConnection.OnServerRequest"/>). Spec-derived, NOT
/// probe-confirmed: the probe never observed a real <c>session/request_permission</c> frame
/// (the probe account's turn ended before any tool call — see
/// <c>docs/acp-probe-findings.md</c> §"Permission / elicitation requests"). Mirrors the shape
/// <see cref="Capacitor.Cli.Tests.Unit.Acp.FakeAcpAgent.BuildRequestPermissionFrame"/> already
/// builds for tests. <see cref="ToolCall"/> stays an opaque <see cref="JsonElement"/> — its exact
/// schema is unconfirmed and it is never re-serialized, only forwarded to the server as
/// <see cref="AcpInteractionRequest.ToolInput"/> best-effort (see <c>AcpInteractionBridge</c>).
/// </summary>
public sealed record SessionRequestPermissionParams(
    [property: JsonPropertyName("sessionId")] string              SessionId,
    [property: JsonPropertyName("toolCall")]  JsonElement          ToolCall,
    [property: JsonPropertyName("options")]   PermissionOptionDto[] Options
);

/// <summary>One offered option in a <see cref="SessionRequestPermissionParams"/> — spec-derived, NOT probe-confirmed.</summary>
public sealed record PermissionOptionDto(
    [property: JsonPropertyName("optionId")] string OptionId,
    [property: JsonPropertyName("name")]     string Name,
    [property: JsonPropertyName("kind")]     string Kind
);

/// <summary>
/// Client's JSON-RPC <c>result</c> for a <c>session/request_permission</c> request — spec-derived,
/// NOT probe-confirmed. Mirrors <see cref="Capacitor.Cli.Tests.Unit.Acp.FakeAcpAgent.PermissionOutcomeSelected"/>/
/// <see cref="Capacitor.Cli.Tests.Unit.Acp.FakeAcpAgent.PermissionOutcomeCancelled"/>.
/// </summary>
public sealed record PermissionOutcomeResult(
    [property: JsonPropertyName("outcome")] PermissionOutcomeDto Outcome
);

/// <summary>
/// <c>Outcome</c> is <c>"selected"</c> (with <see cref="OptionId"/> set to the chosen
/// <see cref="PermissionOptionDto.OptionId"/>) or <c>"cancelled"</c> (denial/timeout/agent-exit) —
/// spec-derived, NOT probe-confirmed.
/// </summary>
public sealed record PermissionOutcomeDto(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("optionId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                             string? OptionId = null
);

/// <summary>
/// Agent→client <c>elicitation/create</c> request params — the STABILIZED ACP shape
/// (agent-client-protocol #1779, 2026-07-24; <c>schema/v1/schema.json</c>
/// <c>CreateElicitationRequest</c>), replacing the pre-stabilization draft this daemon was
/// originally modeled on (whose <c>options</c> array never existed on any stabilized wire).
/// Mode variants: <c>"form"</c> carries <see cref="RequestedSchema"/>; <c>"url"</c> carries
/// <see cref="ElicitationId"/> + <see cref="Url"/> (unsupported by this client — cancelled).
/// Scope variants: session-scoped (<see cref="SessionId"/>) or request-scoped
/// (<see cref="RequestId"/>, unsupported — cancelled). Every member is nullable at the DTO
/// layer: <c>Capacitor.Cli.Daemon.Acp.AcpInteractionBridge</c> owns ALL semantic validation
/// (a non-nullable C# string would not guarantee the wire supplied one). The daemon now
/// advertises the <c>elicitation</c> client capability (form mode only — see
/// <see cref="ElicitationCapabilities"/> and <c>AcpHostedAgentRuntime</c>'s initialize call
/// sites), the end-to-end multi-select work having shipped; this lane also still answers
/// UNSOLICITED frames spec-correctly instead of with the old malformed <c>{outcome}</c> result.
/// <see cref="RequestedSchema"/> is forwarded to the server verbatim for audit (capped
/// server-side); the daemon's own <c>ElicitationSchemaClassifier</c> parses it separately.
/// </summary>
public sealed record ElicitationCreateParams(
    [property: JsonPropertyName("sessionId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                string?      SessionId = null,
    [property: JsonPropertyName("requestId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                JsonElement? RequestId = null,
    [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                string?      Message = null,
    [property: JsonPropertyName("mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                string?      Mode = null,
    [property: JsonPropertyName("requestedSchema"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                JsonElement? RequestedSchema = null,
    [property: JsonPropertyName("elicitationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                string?      ElicitationId = null,
    [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                string?      Url = null
);

/// <summary>
/// Client's JSON-RPC <c>result</c> for <c>elicitation/create</c> — the STABILIZED
/// <c>CreateElicitationResponse</c> shape: <see cref="ActionName"/> is <c>"accept"</c> (with
/// <see cref="Content"/> keyed by the requested schema's property name; a value is a JSON string
/// or an array of JSON strings), <c>"decline"</c>, or <c>"cancel"</c> (both content-free — the
/// member is OMITTED, not null). Deliberately a separate type from the permission path's
/// <c>PermissionOutcomeResult</c>: the two are different protocol objects, and sharing the
/// builder is exactly how the obsolete <c>{outcome}</c> elicitation result shape leaked in.
/// </summary>
public sealed record ElicitationResponse(
    [property: JsonPropertyName("action")]  string ActionName,
    [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                            Dictionary<string, JsonElement>? Content = null
);
