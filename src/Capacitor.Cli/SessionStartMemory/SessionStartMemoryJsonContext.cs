using System.Text.Json.Serialization;

namespace Capacitor.Cli.SessionStartMemory;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SessionStartMemoryEntry[]))]
[JsonSerializable(typeof(SessionStartMemoryIndexResponse))]
[JsonSerializable(typeof(GuidelinesResponse))]
[JsonSerializable(typeof(SessionStartMemoryStoreRecord))]
[JsonSerializable(typeof(SessionStartMemoryStoreMetadata))]
[JsonSerializable(typeof(ClaudeMemoryEnvelope))]
[JsonSerializable(typeof(CodexMemoryEnvelope))]
[JsonSerializable(typeof(CursorMemoryEnvelope))]
[JsonSerializable(typeof(CopilotMemoryEnvelope))]
[JsonSerializable(typeof(GeminiMemoryEnvelope))]
[JsonSerializable(typeof(AntigravityMemoryEnvelope))]
internal partial class SessionStartMemoryJsonContext : JsonSerializerContext;

/// <summary>Shape of <c>GET /api/repositories/{hash}/guidelines</c>.
/// The response is snake_case (<c>JsonNamingPolicy.SnakeCaseLower</c> server-side);
/// pinned server-side by the guidelines endpoint's wire-contract test.</summary>
internal sealed record GuidelinesResponse(
    [property: JsonPropertyName("repo_hash")] string? RepoHash,
    [property: JsonPropertyName("guidelines")] GuidelineRow[]? Guidelines);

internal sealed record GuidelineRow(
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("text")] string? Text);

internal sealed record HookMemoryOutput(
    [property: JsonPropertyName("hookEventName")] string HookEventName,
    [property: JsonPropertyName("additionalContext")] string AdditionalContext);

internal sealed record HookSpecificMemoryOutput(
    [property: JsonPropertyName("hookSpecificOutput")] HookMemoryOutput HookSpecificOutput);

internal sealed record ClaudeMemoryEnvelope(
    [property: JsonPropertyName("hookSpecificOutput")] HookMemoryOutput HookSpecificOutput);

internal sealed record CodexMemoryEnvelope(
    [property: JsonPropertyName("continue")] bool Continue,
    [property: JsonPropertyName("hookSpecificOutput")] HookMemoryOutput HookSpecificOutput);

internal sealed record CursorMemoryEnvelope([property: JsonPropertyName("additional_context")] string? AdditionalContext = null);
internal sealed record CopilotMemoryEnvelope([property: JsonPropertyName("additionalContext")] string? AdditionalContext = null);
internal sealed record GeminiMemoryEnvelope([property: JsonPropertyName("hookSpecificOutput")] HookMemoryOutput? HookSpecificOutput = null);

internal sealed record AntigravityMemoryEnvelope([property: JsonPropertyName("injectSteps")] AntigravityMemoryStep[]? InjectSteps = null);
internal sealed record AntigravityMemoryStep([property: JsonPropertyName("userMessage")] string UserMessage);
