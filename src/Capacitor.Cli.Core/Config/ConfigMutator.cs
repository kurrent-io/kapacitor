using System.Text.Json;

namespace Capacitor.Cli.Core.Config;

/// The ONE writer of config.json (spec decision 10). Field-scoped mutation under
/// ConfigFileLock: lock → re-read fresh → migrate in memory → apply the caller's mutation →
/// publish via UNIQUE temp + rename. The critical section is synchronous on one thread —
/// ConfigFileLock is a thread-affine named Mutex (WaitOne/ReleaseMutex), so no await may
/// occur while it is held; async callers are wrapped in Task.Run here.
public static class ConfigMutator {
    public static Task<ProfileConfig> MutateAsync(
            Func<ProfileConfig, ProfileConfig> mutate, CancellationToken ct = default) =>
        Task.Run(() => Mutate(mutate), ct);

    public static ProfileConfig Mutate(Func<ProfileConfig, ProfileConfig> mutate) {
        var path = AppConfig.GetConfigPath();
        var dir  = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        using (ConfigFileLock.Acquire(path)) {
            var current = LoadPure(path);           // fresh re-read + in-memory migration
            var next    = mutate(current);
            Publish(path, next);
            return next;
        }
    }

    /// Pure load: parse + migrate in memory, NEVER writes (decision 10 — the legacy
    /// LoadProfileConfig persisted the v1→v2 migration during load, which under this API
    /// would recursively acquire the same thread-affine mutex).
    public static ProfileConfig LoadPure(string path) {
        if (!File.Exists(path))
            return new() { Profiles = new() { ["default"] = new() } };

        string json;
        try {
            json = File.ReadAllText(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new() { Profiles = new() { ["default"] = new() } };
        }

        try {
            return ConfigMigration.MigrateIfNeeded(json).Config;
        } catch (JsonException) {
            return new() { Profiles = new() { ["default"] = new() } };
        }
    }

    static void Publish(string path, ProfileConfig config) {
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tmp,
            JsonSerializer.SerializeToUtf8Bytes(config, ProfileConfigJsonContextIndented.Default.ProfileConfig));
        File.Move(tmp, path, overwrite: true);
    }
}
