using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Config;

public record ProfileConfig {
    [JsonPropertyName("version")]
    public int Version { get; init; } = 2;

    [JsonPropertyName("active_profile")]
    public string ActiveProfile { get; init; } = "default";

    [JsonPropertyName("profiles")]
    public Dictionary<string, Profile> Profiles { get; init; } = new();

    [JsonPropertyName("profile_bindings")]
    public Dictionary<string, string> ProfileBindings { get; init; } = new();

    /// <summary>
    /// Path-prefix remaps applied to historic transcript cwds before repository
    /// detection. Useful when a local repo directory has been renamed (e.g.
    /// <c>~/dev/kapacitor-cli → ~/dev/kcap-cli</c>) so old sessions can still
    /// be resolved to their org/repo during <c>kcap import</c>.
    /// Match is a path-boundary prefix (cwd == from || cwd starts with from + "/");
    /// longest-from wins when multiple rules could apply.
    /// </summary>
    [JsonPropertyName("cwd_remap")]
    public CwdRemap[] CwdRemap { get; init; } = [];

    // stable machine identity for machine-tagged memories. Generated once by
    // MachineIdProvider; never rotated (rotation orphans previously tagged memories).
    [JsonPropertyName("machine_id")]
    public string? MachineId { get; init; }
}

public record CwdRemap {
    [JsonPropertyName("from")]
    public string From { get; init; } = "";

    [JsonPropertyName("to")]
    public string To { get; init; } = "";
}

public record Profile {
    [JsonPropertyName("server_url")]
    public string? ServerUrl { get; init; }

    [JsonPropertyName("daemon")]
    public DaemonSettings? Daemon { get; init; }

    [JsonPropertyName("default_visibility")]
    public string DefaultVisibility { get; init; } = "org_public";

    [JsonPropertyName("disable_session_guidelines")]
    public bool? DisableSessionGuidelines { get; init; }

    /// <summary>
    /// when true, kcap skips injecting the team-memory index at SessionStart.
    /// Independent of <see cref="DisableSessionGuidelines"/> so the recurring-lessons and
    /// memory-index injections can be toggled separately.
    /// </summary>
    [JsonPropertyName("disable_memory_index")]
    public bool? DisableMemoryIndex { get; init; }

    /// <summary>
    /// When true, kcap keeps <c>ANTHROPIC_API_KEY</c> / <c>OPENAI_API_KEY</c>
    /// in the spawn environment for headless agent CLIs (title generation,
    /// summaries, judges). Default <c>false</c> scrubs them so subscription
    /// auth (claude.ai / ChatGPT account) is used instead.
    /// Override at runtime with <c>KCAP_USE_PROVIDER_API_KEY=1</c>.
    /// </summary>
    [JsonPropertyName("use_provider_api_key")]
    public bool UseProviderApiKey { get; init; }

    [JsonPropertyName("update_check")]
    public bool UpdateCheck { get; init; } = true;

    [JsonPropertyName("update_channel")]
    public string UpdateChannel { get; init; } = "latest";

    [JsonPropertyName("excluded_repos")]
    public string[] ExcludedRepos { get; init; } = [];

    [JsonPropertyName("excluded_paths")]
    public string[] ExcludedPaths { get; init; } = [];

    [JsonPropertyName("remotes")]
    public string[] Remotes { get; init; } = [];

    /// <summary>
    /// GitHub org/owner used by <c>kcap import --org</c> to filter sessions by
    /// their git-remote owner. Decoupled from the profile name: under WorkOS the
    /// profile is named after the tenant slug, which is not a GitHub org, so the
    /// org to scope on is chosen from discovered repos (or passed as
    /// <c>--org &lt;owner&gt;</c>) and remembered here for subsequent bare <c>--org</c> runs.
    /// </summary>
    [JsonPropertyName("import_org")]
    public string? ImportOrg { get; init; }

    [JsonPropertyName("flows")]
    public FlowsSettings? Flows { get; init; }

    /// <summary>AI-1677: the saved reviewer-vendor preference, with null/blank/whitespace
    /// defensively read as "no preference" — a blank treated as set would consume the single
    /// preference retry with an effectively vendor-less request and re-fail identically.</summary>
    public string? EffectiveReviewerVendorPreference() =>
        string.IsNullOrWhiteSpace(Flows?.ReviewerVendor) ? null : Flows!.ReviewerVendor!.Trim();
}

public record FlowsSettings {
    [JsonPropertyName("reviewer_vendor")]
    public string? ReviewerVendor { get; init; }
}

/// <summary>Repo-level .kcap.json committed to VCS.</summary>
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
