using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestReviewerDto {
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("availability")]
    public required string Availability { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
    [JsonPropertyName("url")]
    public string? Url { get; init; }
    [JsonPropertyName("actor")]
    public PullRequestActorDto? Actor { get; init; }
    [JsonPropertyName("requested")]
    public bool? Requested { get; init; }
    [JsonPropertyName("review_state")]
    public string? ReviewState { get; init; }
    [JsonPropertyName("submitted_at")]
    public DateTime? SubmittedAt { get; init; }
}
