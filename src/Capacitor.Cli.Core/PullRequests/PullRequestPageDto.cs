using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestPageDto<T> where T : class {
    [JsonPropertyName("snapshot_id")]
    public required string SnapshotId { get; init; }
    [JsonPropertyName("snapshot_started_at")]
    public required DateTime SnapshotStartedAt { get; init; }
    [JsonPropertyName("snapshot_completed_at")]
    public required DateTime SnapshotCompletedAt { get; init; }
    [JsonPropertyName("coverage")]
    public required string Coverage { get; init; }
    [JsonPropertyName("coverage_reason")]
    public string? CoverageReason { get; init; }
    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; init; }
    [JsonPropertyName("total")]
    public required PullRequestCountDto Total { get; init; }
    [JsonPropertyName("excluded_by_filter")]
    public required PullRequestCountDto ExcludedByFilter { get; init; }
    [JsonPropertyName("items")]
    public required T[] Items { get; init; }
    [JsonPropertyName("page_cursor")]
    public required string PageCursor { get; init; }
    [JsonPropertyName("has_more")]
    public required bool HasMore { get; init; }
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }
}
