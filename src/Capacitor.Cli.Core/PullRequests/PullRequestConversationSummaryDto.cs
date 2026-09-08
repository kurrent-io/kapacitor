using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestConversationSummaryDto {
    [JsonPropertyName("availability")]
    public required PullRequestAvailabilityDto Availability { get; init; }
    [JsonPropertyName("count")]
    public PullRequestCountDto? Count { get; init; }
}
