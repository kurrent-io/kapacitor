using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestThreadDto {
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("availability")]
    public required string Availability { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
    [JsonPropertyName("url")]
    public string? Url { get; init; }
    [JsonPropertyName("is_resolved")]
    public bool? IsResolved { get; init; }
    [JsonPropertyName("is_outdated")]
    public bool? IsOutdated { get; init; }
    [JsonPropertyName("path")]
    public string? Path { get; init; }
    [JsonPropertyName("diff_side")]
    public string? DiffSide { get; init; }
    [JsonPropertyName("start_diff_side")]
    public string? StartDiffSide { get; init; }
    [JsonPropertyName("line")]
    public int? Line { get; init; }
    [JsonPropertyName("start_line")]
    public int? StartLine { get; init; }
    [JsonPropertyName("original_line")]
    public int? OriginalLine { get; init; }
    [JsonPropertyName("original_start_line")]
    public int? OriginalStartLine { get; init; }
    [JsonPropertyName("subject_type")]
    public string? SubjectType { get; init; }
    [JsonPropertyName("diff_hunk")]
    public string? DiffHunk { get; init; }
    [JsonPropertyName("hunk_truncated")]
    public bool HunkTruncated { get; init; }
    [JsonPropertyName("root_comment")]
    public PullRequestCommentDto? RootComment { get; init; }
    [JsonPropertyName("comments")]
    public PullRequestCountDto? Comments { get; init; }
}
