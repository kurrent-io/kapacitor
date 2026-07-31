using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the consent control frames (AI-1623). snake_case on the wire; shared
/// verbatim by the daemon, the CLI, and the future desktop app.
public sealed record ConsentPendingDto(
    string RequestId, string? Requester, string Kind, string RepoPath, string Vendor,
    string RequestedAt, int TimeoutSeconds);

public sealed record ConsentResolveDto(string RequestId, string Decision, ConsentRuleDto? SaveRule);

public sealed record ConsentRuleDto(string Action, string? Requester, string? Kind, string? Repo, string? Vendor);

public sealed record ConsentPolicyDto(string Default, int PromptTimeoutSeconds, List<ConsentRuleDto> Rules);

/// Ok = did the primary operation apply; Error = failure detail, OR a partial-failure warning
/// when Ok=true (e.g. ConsentResolve's decision was applied but its optional save_rule was
/// rejected — the resolution itself is not conflated with that secondary failure).
public sealed record ConsentAckDto(bool Ok, string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ConsentPendingDto))]
[JsonSerializable(typeof(ConsentResolveDto))]
[JsonSerializable(typeof(ConsentPolicyDto))]
[JsonSerializable(typeof(ConsentAckDto))]
public partial class ConsentIpcJsonContext : JsonSerializerContext;
