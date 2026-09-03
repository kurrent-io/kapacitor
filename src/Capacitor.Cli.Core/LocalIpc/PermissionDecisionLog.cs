using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// One line of permission-decisions.jsonl. Outcome: allow|deny|withdrawn.
/// Source: app|server|agent_gone|no_ui|policy.
public sealed record PermissionDecisionRecord(
    string DecidedAt, string AgentId, string SessionId, string Vendor,
    string ToolName, string Outcome, string Source);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(PermissionDecisionRecord))]
public partial class PermissionDecisionJsonContext : JsonSerializerContext;
