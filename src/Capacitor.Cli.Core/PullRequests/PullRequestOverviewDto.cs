using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestOverviewDto {
    [JsonPropertyName("title")]
    public string? Title { get; init; }
    [JsonPropertyName("url")]
    public string? Url { get; init; }
    [JsonPropertyName("lifecycle")]
    public string? Lifecycle { get; init; }
    [JsonPropertyName("is_draft")]
    public bool? IsDraft { get; init; }
    [JsonPropertyName("head_ref")]
    public string? HeadRef { get; init; }
    [JsonPropertyName("base_ref")]
    public string? BaseRef { get; init; }
    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; init; }
    [JsonPropertyName("description")]
    public string? Description { get; init; }
    [JsonPropertyName("description_truncated")]
    public bool DescriptionTruncated { get; init; }
    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; init; }
    [JsonPropertyName("review_decision")]
    public string? ReviewDecision { get; init; }
    [JsonPropertyName("access_checked_for")]
    public string? AccessCheckedFor { get; init; }
    [JsonPropertyName("checks")]
    public PullRequestChecksSummaryDto? Checks { get; init; }
    [JsonPropertyName("reviews")]
    public PullRequestReviewsSummaryDto? Reviews { get; init; }
    [JsonPropertyName("conversation")]
    public PullRequestConversationSummaryDto? Conversation { get; init; }
}
