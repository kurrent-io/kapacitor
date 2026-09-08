using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestChecksSummaryDto {
    [JsonPropertyName("availability")]
    public required PullRequestAvailabilityDto Availability { get; init; }
    [JsonPropertyName("rollup")]
    public string? Rollup { get; init; }
    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; init; }
    [JsonPropertyName("counts")]
    public Dictionary<string, PullRequestCountDto>? Counts { get; init; }
}
