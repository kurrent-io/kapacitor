using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestCountDto {
    [JsonPropertyName("value")]
    public int? Value { get; init; }
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
}
