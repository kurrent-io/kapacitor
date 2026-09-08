using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestReviewsSummaryDto {
    [JsonPropertyName("availability")]
    public required PullRequestAvailabilityDto Availability { get; init; }
    [JsonPropertyName("published")]
    public PullRequestCountDto? Published { get; init; }
    [JsonPropertyName("approved")]
    public PullRequestCountDto? Approved { get; init; }
    [JsonPropertyName("changes_requested")]
    public PullRequestCountDto? ChangesRequested { get; init; }
    [JsonPropertyName("outstanding_users")]
    public PullRequestCountDto? OutstandingUsers { get; init; }
    [JsonPropertyName("outstanding_teams")]
    public PullRequestCountDto? OutstandingTeams { get; init; }
}
