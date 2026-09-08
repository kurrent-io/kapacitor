using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestEnvelopeDto<T> where T : class {
    [JsonPropertyName("status")]
    public required string Status { get; init; }
    [JsonPropertyName("subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PullRequestSubjectDto? Subject { get; init; }
    [JsonPropertyName("data")]
    public T? Data { get; init; }
    [JsonPropertyName("fetched_at")]
    public DateTime? FetchedAt { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
    [JsonPropertyName("retry_at")]
    public DateTime? RetryAt { get; init; }
    [JsonPropertyName("poll_after_seconds")]
    public int PollAfterSeconds { get; init; }
    [JsonPropertyName("access_valid_for_seconds")]
    public int AccessValidForSeconds { get; init; }
    [JsonPropertyName("access_failure")]
    public string? AccessFailure { get; init; }
}
