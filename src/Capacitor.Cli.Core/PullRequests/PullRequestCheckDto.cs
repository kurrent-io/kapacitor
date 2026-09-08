using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestCheckDto {
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("availability")]
    public required string Availability { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
    [JsonPropertyName("url")]
    public string? Url { get; init; }
    [JsonPropertyName("name")]
    public string? Name { get; init; }
    [JsonPropertyName("app_name")]
    public string? AppName { get; init; }
    [JsonPropertyName("app_id")]
    public string? AppId { get; init; }
    [JsonPropertyName("suite_id")]
    public string? SuiteId { get; init; }
    [JsonPropertyName("source")]
    public string? Source { get; init; }
    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }
    [JsonPropertyName("status")]
    public string? Status { get; init; }
    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; init; }
    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; init; }
    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; init; }
    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; init; }
}
