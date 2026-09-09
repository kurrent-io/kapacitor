namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>What a minted handle resolves to. <see cref="Items"/> freezes a whole section; <see cref="After"/> continues a GraphQL connection.</summary>
public sealed record GitHubCliCursorEntry(string SnapshotId, string Key, DateTime StartedAt, string? HeadSha, object? Items = null, int Offset = 0, string? After = null, bool Capped = false);
