using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Config;

public record ProfileConfig {
    [JsonPropertyName("version")]
    public int Version { get; init; } = 2;

    /// <summary>The profile every fallback lands on. Also the name <see cref="ActiveProfile"/>
    /// defaults to, so the two can never disagree.</summary>
    public const string DefaultName = "default";

    [JsonPropertyName("active_profile")]
    public string ActiveProfile { get; init; } = DefaultName;

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

    /// <summary>What a config with nothing configured looks like: one empty <see cref="DefaultName"/>
    /// profile. This is what a missing or unreadable config.json degrades to, so a machine that has
    /// never run <c>kcap setup</c> still has a profile to read settings off.</summary>
    public static ProfileConfig Fresh() => new() { Profiles = new() { [DefaultName] = new() } };

    /// <summary>The active profile's NAME, with a blank <c>active_profile</c> normalised back to
    /// <see cref="DefaultName"/> — a hand-edited or half-migrated file can carry one, and every
    /// caller that reads the raw field has to make the same correction.</summary>
    [JsonIgnore]
    public string ActiveName => string.IsNullOrWhiteSpace(ActiveProfile) ? DefaultName : ActiveProfile;

    /// <summary>The profile <see cref="ActiveName"/> names — null when it names one this config has
    /// no entry for.</summary>
    [JsonIgnore]
    public Profile? Active => Profiles.GetValueOrDefault(ActiveName);
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
    /// when true, kcap skips injecting the SessionStart work-items nudge (the standing
    /// guidance that tells the agent to register the session with a work item and declare
    /// blockers/dependencies as it works). Independent of the memory-index and guidelines
    /// opt-outs so each SessionStart injection can be toggled separately.
    /// </summary>
    [JsonPropertyName("disable_workitems_nudge")]
    public bool? DisableWorkItemsNudge { get; init; }

    /// <summary>
    /// when true, kcap does not advertise the coordination-notices capability at SessionStart, so the
    /// server injects no coordination notices (heads-up about others' in-flight work that may overlap
    /// yours) — they still reach the notification centre and Slack. Independent of the other SessionStart
    /// opt-outs. Mirrors <see cref="DisableMemoryIndex"/>.
    /// </summary>
    [JsonPropertyName("disable_coordination_notices")]
    public bool? DisableCoordinationNotices { get; init; }

    /// <summary>
    /// when true, kcap emits no new-harness setup nudges — neither the SessionStart nudge nor the
    /// interactive CLI stderr notice — for harnesses that are installed but not yet wired into kcap.
    /// The total "never ask about any harness" switch, distinct from a per-vendor
    /// <c>kcap harness dismiss</c>. Independent of the other SessionStart opt-outs.
    /// </summary>
    [JsonPropertyName("disable_harness_nudge")]
    public bool? DisableHarnessNudge { get; init; }

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

    [JsonPropertyName("skills")]
    public SkillsSettings? Skills { get; init; }

    /// <summary>
    /// Provider + canonical server identity learned at the last successful sign-in (Plan C's
    /// commit boundary). Additive and READ-only in Plan B — nothing writes it yet; only
    /// <c>OnboardingGate</c> reads it, and only when the stamped server still matches.
    /// </summary>
    [JsonPropertyName("auth_provider")]
    public AuthProviderStamp? AuthProvider { get; init; }

    /// <summary>The saved reviewer-vendor preference, with null/blank/whitespace defensively
    /// read as "no preference" — a blank treated as set would consume the single preference
    /// retry with an effectively vendor-less request and re-fail identically.</summary>
    public string? EffectiveReviewerVendorPreference() =>
        string.IsNullOrWhiteSpace(Flows?.ReviewerVendor) ? null : Flows!.ReviewerVendor!.Trim();
}

/// <summary>Provider + the canonical server identity it was learned for — a stamp for a
/// DIFFERENT server (after a <c>server_url</c> change) must never be trusted as current.</summary>
public sealed record AuthProviderStamp(
    [property: JsonPropertyName("provider")]   string Provider,
    [property: JsonPropertyName("server_url")] string ServerUrl);

public record FlowsSettings {
    [JsonPropertyName("reviewer_vendor")]
    public string? ReviewerVendor { get; init; }
}

public record SkillsSettings {
    /// <summary>when true, the Claude session-start hook spawns a detached, self-throttling
    /// `kcap skills sync --auto` so centrally revoked or re-approved skills reach this machine
    /// without a manual sync. Off by default.</summary>
    [JsonPropertyName("auto_sync")]
    public bool? AutoSync { get; init; }
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
