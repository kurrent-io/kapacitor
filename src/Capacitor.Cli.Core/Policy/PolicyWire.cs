namespace Capacitor.Cli.Core.Policy;

using System.Text;

public static class PolicySeams {
    public const string ClaudePreToolUse = "claude_pre_tool_use";
    public const string ClaudePermissionRequest = "claude_permission_request";
    public const string HostedClaudePermission = "hosted_claude_permission";
    public const string AcpRequestPermission = "acp_request_permission";
}

public sealed record PolicyActionV1(
    string Kind, string Vendor, string? Command, bool Analyzed, string[][]? Segments,
    string[]? Paths, string? Host, int? Port, string? Url, string? Server, string? Tool,
    string? RawToolName, string? RawPayload, bool RawPayloadTruncated, string? Justification);

public sealed record PolicyMatchedRuleV1(string Scope, int RuleIndex, string Outcome, string? Reason);

public sealed record PolicyDecisionEventV1(
    string SessionId, string? AgentId, string Vendor, string Seam, string SnapshotId, string EngineVersion,
    string EvaluationMode, string RequestedOutcome, string EffectiveOutcome, PolicyActionV1 Action,
    PolicyMatchedRuleV1[] MatchedRules, bool Degraded, string? FailureClass,
    string? CorrelationId, bool CorrelationAmbiguous, string DecidedAt);

public sealed record PolicySnapshotUploadV1(
    string SessionId, string SnapshotId, string EngineVersion, bool Degraded, string[] Degradations,
    PolicySnapshotDocV1[] Documents);

public sealed record PolicySnapshotDocV1(string Scope, string SourcePath, string Content);

public static class PolicyWire {
    public const int MaxRawPayloadBytes = 16 * 1024;

    public static PolicyActionV1 ToWire(CanonicalAction a) {
        var raw = a.RawPayloadJson;
        var truncated = false;
        if (raw is not null && Encoding.UTF8.GetByteCount(raw) > MaxRawPayloadBytes) {
            raw = raw[..Math.Min(raw.Length, MaxRawPayloadBytes)];
            truncated = true;
        }
        return new(
            a.Kind.ToString(), a.Vendor, a.Command, a.Analyzed,
            a.Kind is ActionKind.Shell && a.Analyzed ? [.. a.Segments.Select(s => s.Argv.ToArray())] : null,
            a.Paths.Count > 0 ? [.. a.Paths] : null,
            a.Host, a.Port, a.Url, a.Server, a.Tool, a.RawToolName, raw, truncated, a.Justification);
    }

    public static PolicyMatchedRuleV1[] ToWire(IReadOnlyList<MatchedRuleRef> rules) =>
        [.. rules.Select(r => new PolicyMatchedRuleV1(r.Scope.ToString(), r.RuleIndex, r.Outcome.ToString().ToLowerInvariant(), r.Reason))];

    public static PolicySnapshotUploadV1 ToUpload(string sessionId, PolicySnapshot snapshot) => new(
        sessionId, snapshot.Id, PolicyEngine.Version, snapshot.Degraded, [.. snapshot.Degradations],
        [.. snapshot.Documents.Select(d => new PolicySnapshotDocV1(d.Scope.ToString(), d.SourcePath, d.Content))]);
}
