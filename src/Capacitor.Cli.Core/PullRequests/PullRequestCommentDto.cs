using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestCommentDto {
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("availability")]
    public required string Availability { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
    [JsonPropertyName("url")]
    public string? Url { get; init; }
    [JsonPropertyName("author")]
    public PullRequestActorDto? Author { get; init; }
    [JsonPropertyName("body")]
    public string? Body { get; init; }
    [JsonPropertyName("body_truncated")]
    public bool BodyTruncated { get; init; }
    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; init; }
    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; init; }
    [JsonPropertyName("published_at")]
    public DateTime? PublishedAt { get; init; }
    [JsonPropertyName("reply_to_id")]
    public string? ReplyToId { get; init; }
}
