using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestAvailabilityDto {
    [JsonPropertyName("status")]
    public required string Status { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
    [JsonPropertyName("fetched_at")]
    public DateTime? FetchedAt { get; init; }
}
