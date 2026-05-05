using System.Text.Json;
using System.Text.Json.Nodes;

namespace kapacitor.Config;

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

        // Check if already V2
        if (node["version"]?.GetValue<int>() is 2) {
            var v2 = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

            return new(v2, WasMigrated: false, ShouldPersist: false);
        }

        // V1 → V2: read old flat fields, build default profile
        var v1 = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.KapacitorConfig)
         ?? new KapacitorConfig();

        var defaultProfile = new Profile {
            ServerUrl         = v1.ServerUrl,
            Daemon            = v1.Daemon,
            DefaultVisibility = v1.DefaultVisibility,
            UpdateCheck       = v1.UpdateCheck,
            ExcludedRepos     = v1.ExcludedRepos
        };

        var config = new ProfileConfig {
            ActiveProfile   = "default",
            Profiles        = new() { ["default"] = defaultProfile },
            ProfileBindings = []
        };

        return new(config, WasMigrated: true, ShouldPersist: true);
    }
}
