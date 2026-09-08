using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestActorDto {
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("login")]
    public string? Login { get; init; }
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
