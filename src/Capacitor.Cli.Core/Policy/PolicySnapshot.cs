namespace Capacitor.Cli.Core.Policy;

public sealed record PolicyScopeDocument(PolicyScope Scope, string SourcePath, string Content, PolicyDocument Document);

public sealed record PolicySnapshot(
    string Id, IReadOnlyList<PolicyScopeDocument> Documents, bool Degraded, IReadOnlyList<string> Degradations) {
    public bool IsEmpty => Documents.Count == 0 && !Degraded;
    public static readonly PolicySnapshot Empty = new("empty", [], false, []);
}
