using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestErrorDto {
    [JsonPropertyName("error")]
    public required string Error { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
