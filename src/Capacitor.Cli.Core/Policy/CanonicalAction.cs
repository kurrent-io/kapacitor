namespace Capacitor.Cli.Core.Policy;

public enum ActionKind { Shell, FileEdit, FileRead, Network, McpTool, Other }

public sealed record ShellSegment(IReadOnlyList<string> Argv) {
    public bool Equals(ShellSegment? other) => other is not null && Argv.SequenceEqual(other.Argv);
    public override int GetHashCode() => Argv.Aggregate(0, HashCode.Combine);
}

/// <summary>
/// A vendor-neutral view of one tool call. Normalizers guarantee the per-kind invariants
/// (non-empty Paths for file kinds, Host for network, Server+Tool for mcp_tool); a payload
/// that cannot satisfy them is emitted as kind Other instead, so no evaluation is skipped.
/// </summary>
public sealed record CanonicalAction {
    public required ActionKind Kind { get; init; }
    public required string Vendor { get; init; }
    public string? Cwd { get; init; }
    public string? Command { get; init; }
    public bool Analyzed { get; init; }
    public IReadOnlyList<ShellSegment> Segments { get; init; } = [];
    public IReadOnlyList<string> Paths { get; init; } = [];
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Url { get; init; }
    public string? Server { get; init; }
    public string? Tool { get; init; }
    public string? RawToolName { get; init; }
    public string? RawPayloadJson { get; init; }
    public string? Justification { get; init; }
}

public abstract record ActionComponent;
public sealed record ShellSegmentComponent(ShellSegment Segment) : ActionComponent;
public sealed record RawShellComponent(string Command) : ActionComponent;
public sealed record PathComponent(string Path) : ActionComponent;
public sealed record HostComponent(string Host, int? Port) : ActionComponent;
public sealed record McpToolComponent(string Server, string Tool) : ActionComponent;
public sealed record OtherToolComponent(string ToolName) : ActionComponent;
public sealed record SentinelComponent : ActionComponent;

public static class PolicyComponents {
    /// <summary>What deny/ask rules match (any hit decides). Never empty.</summary>
    public static IReadOnlyList<ActionComponent> RestrictionOf(CanonicalAction a) => a.Kind switch {
        ActionKind.Shell when a.Analyzed && a.Segments.Count > 0 =>
            [.. a.Segments.Select(s => (ActionComponent)new ShellSegmentComponent(s))],
        ActionKind.Shell => [new RawShellComponent(a.Command ?? "")],
        ActionKind.FileEdit or ActionKind.FileRead when a.Paths.Count > 0 =>
            [.. a.Paths.Select(p => (ActionComponent)new PathComponent(p))],
        ActionKind.Network when a.Host is { Length: > 0 } => [new HostComponent(a.Host, a.Port)],
        ActionKind.McpTool when a.Server is { Length: > 0 } && a.Tool is { Length: > 0 } =>
            [new McpToolComponent(a.Server, a.Tool)],
        ActionKind.Other when a.RawToolName is { Length: > 0 } => [new OtherToolComponent(a.RawToolName)],
        _ => [new SentinelComponent()],
    };

    /// <summary>What allow rules must fully cover. Empty = never allow-eligible.</summary>
    public static IReadOnlyList<ActionComponent> CoverageOf(CanonicalAction a) => a switch {
        { Kind: ActionKind.Shell, Analyzed: false } => [],
        { Kind: ActionKind.Shell } when a.Segments.Count == 0 => [],
        { Kind: ActionKind.Other } when a.RawToolName is not { Length: > 0 } => [],
        _ when RestrictionOf(a) is [SentinelComponent] => [],
        _ => RestrictionOf(a),
    };
}
