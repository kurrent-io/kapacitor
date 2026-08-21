using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Config;

public static class ConfigMigration {
    public record MigrationResult(ProfileConfig Config, bool WasMigrated, bool ShouldPersist);

    static MigrationResult FreshDefault() =>
        new(new() { Profiles = new() { ["default"] = new() } }, WasMigrated: true, ShouldPersist: false);

    public static MigrationResult MigrateIfNeeded(string json) {
        JsonNode? parsed;

        try {
            parsed = JsonNode.Parse(json);
        } catch (JsonException) {
            return FreshDefault();
        }

        if (parsed is not JsonObject node)
            return FreshDefault();

        // A v1 config predates versioning and so carries no "version" key at all. Anything that
        // has one is v2 — including a value we can't read: reinterpreting that as v1 would rewrite
        // the file from flat fields it doesn't have and drop the profiles map. An unreadable
        // version instead surfaces as the JsonException the caller already handles.
        if (node.ContainsKey("version")) {
            var v2 = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

            return new(ApplyDefaults(v2, node), WasMigrated: false, ShouldPersist: false);
        }

        // V1 → V2: read old flat fields, build default profile. The v1 flat keys sit
        // at the same paths ApplyDefaults reads ("update_check", "daemon.max_agents"),
        // so the raw node doubles as the default profile's payload.
        var v1 = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.LegacyV1Config)
         ?? new LegacyV1Config();

        var defaultProfile = new Profile {
            ServerUrl         = v1.ServerUrl,
            Daemon            = v1.Daemon,
            DefaultVisibility = v1.DefaultVisibility,
            ExcludedRepos     = v1.ExcludedRepos
        };

        var config = new ProfileConfig {
            ActiveProfile = "default",
            Profiles      = new() { ["default"] = defaultProfile }
        };

        return new(ApplyDefaults(config, node, sharedProfilePayload: true), WasMigrated: true, ShouldPersist: true);
    }

    /// <summary>
    /// Restores the defaults these records declare but never receive from disk.
    /// STJ source-gen builds init-only records through an object initializer that assigns
    /// <c>default(T)</c> for every property absent from the payload, discarding the member
    /// initializers — so a hand-written, truncated, or older-version config.json deserializes
    /// with nulls where the record declares non-null defaults, and every command that touched
    /// one crashed before it could dispatch (including the <c>kcap config set</c> needed to
    /// repair the file).
    /// <para><paramref name="raw"/> supplies the two defaults that differ from
    /// <c>default(T)</c> — <c>update_check</c> and <c>daemon.max_agents</c> — because only the
    /// JSON can tell "absent" from an explicit <c>false</c>/<c>0</c>.
    /// <paramref name="sharedProfilePayload"/> marks the v1 layout, whose profile fields are
    /// the top-level object rather than entries under <c>profiles</c>.</para>
    /// </summary>
    static ProfileConfig ApplyDefaults(ProfileConfig config, JsonObject raw, bool sharedProfilePayload = false) {
        var rawProfiles = raw["profiles"] as JsonObject;
        var profiles    = new Dictionary<string, Profile>();

        foreach (var (name, profile) in config.Profiles ?? new())
            profiles[name] = ApplyDefaults(
                profile ?? new(),
                sharedProfilePayload ? raw : rawProfiles?[name] as JsonObject
            );

        var bindings = new Dictionary<string, string>();

        foreach (var (path, name) in config.ProfileBindings ?? new())
            if (!string.IsNullOrEmpty(name)) bindings[path] = name;

        return config with {
            ActiveProfile   = string.IsNullOrEmpty(config.ActiveProfile) ? "default" : config.ActiveProfile,
            Profiles        = profiles,
            ProfileBindings = bindings,
            CwdRemap = (config.CwdRemap ?? [])
                .Select(r => r is null ? new CwdRemap() : r with { From = r.From ?? "", To = r.To ?? "" })
                .ToArray()
        };
    }

    static Profile ApplyDefaults(Profile profile, JsonObject? raw) => profile with {
        DefaultVisibility = string.IsNullOrEmpty(profile.DefaultVisibility) ? "org_public" : profile.DefaultVisibility,
        UpdateChannel     = string.IsNullOrEmpty(profile.UpdateChannel) ? "latest" : profile.UpdateChannel,
        UpdateCheck       = Scalar(raw, "update_check", true),
        ExcludedRepos     = profile.ExcludedRepos ?? [],
        ExcludedPaths     = profile.ExcludedPaths ?? [],
        Remotes           = profile.Remotes ?? [],
        Daemon = profile.Daemon is null
            ? null
            : profile.Daemon with { MaxAgents = Scalar(raw?["daemon"] as JsonObject, "max_agents", 5) }
    };

    /// <summary>
    /// Reads a scalar straight off the raw node, falling back to <paramref name="fallback"/> when the
    /// key is absent or null. <c>TryGetValue</c> rather than <c>GetValue&lt;T&gt;()</c> keeps the read
    /// total: a type mismatch already surfaces from the deserializer above as a JsonException the
    /// caller handles, and must not be re-raised here as an uncaught InvalidOperationException.
    /// </summary>
    static T Scalar<T>(JsonObject? raw, string key, T fallback) =>
        raw?[key] is JsonValue value && value.TryGetValue<T>(out var parsed) ? parsed : fallback;
}
