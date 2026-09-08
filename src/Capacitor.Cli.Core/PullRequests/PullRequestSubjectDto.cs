using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestSubjectDto {
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }
    [JsonPropertyName("host")]
    public required string Host { get; init; }
    [JsonPropertyName("repo_hash")]
    public required string RepoHash { get; init; }
    [JsonPropertyName("owner")]
    public required string Owner { get; init; }
    [JsonPropertyName("repo_name")]
    public required string RepoName { get; init; }
    [JsonPropertyName("number")]
    public required int Number { get; init; }
}
