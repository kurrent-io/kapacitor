using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestLinkListDto {
    [JsonPropertyName("items")]
    public required PullRequestLinkDto[] Items { get; init; }
}
