using System.Text.Json.Serialization;

namespace kapacitor.Config;

public record ProfileConfig {
    [JsonPropertyName("version")]
    public int Version { get; init; } = 2;

    [JsonPropertyName("active_profile")]
    public string ActiveProfile { get; init; } = "default";

    [JsonPropertyName("profiles")]
    public Dictionary<string, Profile> Profiles { get; init; } = new();

    [JsonPropertyName("profile_bindings")]
    public Dictionary<string, string> ProfileBindings { get; init; } = new();
}

public record Profile {
    [JsonPropertyName("server_url")]
    public string? ServerUrl { get; init; }

    [JsonPropertyName("daemon")]
    public DaemonSettings? Daemon { get; init; }

    [JsonPropertyName("update_check")]
    public bool UpdateCheck { get; init; } = true;

    [JsonPropertyName("default_visibility")]
    public string DefaultVisibility { get; init; } = "org_public";

    [JsonPropertyName("excluded_repos")]
    public string[] ExcludedRepos { get; init; } = [];

    [JsonPropertyName("remotes")]
    public string[] Remotes { get; init; } = [];
}

/// <summary>Repo-level .kapacitor.json committed to VCS.</summary>
public record RepoConfig {
    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    [JsonPropertyName("server_url")]
    public string? ServerUrl { get; init; }
}

[JsonSerializable(typeof(ProfileConfig))]
internal partial class ProfileConfigJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(ProfileConfig))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ProfileConfigJsonContextIndented : JsonSerializerContext;

[JsonSerializable(typeof(RepoConfig))]
internal partial class RepoConfigJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(RepoConfig))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class RepoConfigJsonContextIndented : JsonSerializerContext;
