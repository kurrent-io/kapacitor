using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the consent control frames. snake_case on the wire; shared
/// verbatim by the daemon, the CLI, and the future desktop app.
public sealed record ConsentPendingDto(
    string RequestId, string? Requester, string Kind, string RepoPath, string Vendor,
    string RequestedAt, int TimeoutSeconds, string? RequesterDisplay, string? PromptId);

public sealed record ConsentResolveDto(string RequestId, string Decision, ConsentRuleDto? SaveRule, string? PromptId);

public sealed record ConsentRuleDto(string Action, string? Requester, string? Kind, string? Repo, string? Vendor);

public sealed record ConsentPolicyDto(string Default, int PromptTimeoutSeconds, List<ConsentRuleDto> Rules);

/// Ok = did the primary operation apply; Error = failure detail, OR a partial-failure warning
/// when Ok=true (e.g. ConsentResolve's decision was applied but its optional save_rule was
/// rejected — the resolution itself is not conflated with that secondary failure).
/// RuleSaved: null when the resolve carried no save_rule; true/false = the rule write
/// succeeded/failed — populated on BOTH Ok branches, because save_rule is deliberately
/// persisted before the resolution is attempted.
public sealed record ConsentAckDto(bool Ok, string? Error, bool? RuleSaved);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ConsentPendingDto))]
[JsonSerializable(typeof(ConsentResolveDto))]
[JsonSerializable(typeof(ConsentPolicyDto))]
[JsonSerializable(typeof(ConsentAckDto))]
public partial class ConsentIpcJsonContext : JsonSerializerContext;
