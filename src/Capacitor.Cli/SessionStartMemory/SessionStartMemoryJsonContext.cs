using System.Text.Json.Serialization;

namespace Capacitor.Cli.SessionStartMemory;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SessionStartMemoryEntry[]))]
[JsonSerializable(typeof(SessionStartMemoryStoreRecord))]
[JsonSerializable(typeof(SessionStartMemoryStoreMetadata))]
[JsonSerializable(typeof(ClaudeMemoryEnvelope))]
[JsonSerializable(typeof(CodexMemoryEnvelope))]
[JsonSerializable(typeof(CursorMemoryEnvelope))]
[JsonSerializable(typeof(CopilotMemoryEnvelope))]
[JsonSerializable(typeof(GeminiMemoryEnvelope))]
[JsonSerializable(typeof(GeminiAllowEnvelope))]
[JsonSerializable(typeof(AntigravityMemoryEnvelope))]
internal partial class SessionStartMemoryJsonContext : JsonSerializerContext;

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

/// <summary>
/// Gemini's explicit allow-with-no-context hook result: <c>{"continue":true}</c>.
///
/// <para><b>Why Gemini emits something on the empty path when the other adapters emit nothing.</b>
/// Gemini's hook runner selects the text to parse as <c>stdout.trim() || stderr.trim()</c> — so when a
/// hook writes nothing to stdout it parses the hook's STDERR instead. kcap writes diagnostics there
/// (failed lifecycle POSTs, the auth-lapse notice), which would then be consumed as hook output. The
/// worst case is with memory injection opted OUT: a failed POST could still put kcap text into the
/// model's context. Emitting a payload wins the <c>||</c> and shadows stderr.</para>
///
/// <para>Deliberately carries NO <c>hookSpecificOutput</c> key: Gemini's <c>getAdditionalContext()</c>
/// short-circuits on its own <c>"additionalContext" in hookSpecificOutput</c> guard, so an absent key
/// contributes nothing. <c>{}</c> was rejected because it asserts nothing and relies on every default
/// staying benign; an <c>additionalContext</c>-less <c>hookSpecificOutput</c> was rejected as a shape
/// needing separate verification for no benefit.</para>
/// </summary>
internal sealed record GeminiAllowEnvelope([property: JsonPropertyName("continue")] bool Continue = true);
internal sealed record AntigravityMemoryEnvelope([property: JsonPropertyName("injectSteps")] AntigravityMemoryStep[]? InjectSteps = null);
internal sealed record AntigravityMemoryStep([property: JsonPropertyName("userMessage")] string UserMessage);
