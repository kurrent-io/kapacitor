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
        TryLoadPure(path, out var config);
        return config;
    }

    /// Same pure load as <see cref="LoadPure"/>, but distinguishes genuine ABSENCE (no file —
    /// success, default config) from an UNREADABLE/malformed file (read/parse failure — false, with
    /// the same default config as an out param callers that don't care about the distinction can
    /// still use). Malformed JSON (unparseable, or a non-object root) is unreadable here — it is
    /// deliberately NOT delegated straight to <see cref="ConfigMigration.MigrateIfNeeded"/>, which
    /// absorbs exactly that shape into a silent <c>FreshDefault</c> for <see cref="LoadPure"/>'s own
    /// soft contract; that absorption would otherwise make a gated identity check see malformed
    /// evidence as "nothing configured yet". Callers that must fail closed on corruption rather than
    /// silently treat it the same as absence (e.g. a gated identity check) use this instead of
    /// <see cref="LoadPure"/>.
    public static bool TryLoadPure(string path, out ProfileConfig config) {
        // A directory sitting at the config path is NOT absence — File.Exists alone would say
        // false and this would silently fall through to "nothing configured yet" for what is
        // actually an unreadable/misconfigured location.
        if (Directory.Exists(path)) {
            config = new() { Profiles = new() { ["default"] = new() } };
            return false;
        }

        if (!File.Exists(path)) {
            config = new() { Profiles = new() { ["default"] = new() } };
            return true;
        }

        string json;
        try {
            json = File.ReadAllText(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            config = new() { Profiles = new() { ["default"] = new() } };
            return false;
        }

        // Validate the document itself BEFORE handing it to migration: MigrateIfNeeded treats an
        // unparseable document, or one whose root isn't a JSON object, as v1-absent and silently
        // returns a fresh default — correct for LoadPure, wrong for this method's fail-closed
        // contract.
        try {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("config root is not a JSON object");
        } catch (JsonException) {
            config = new() { Profiles = new() { ["default"] = new() } };
            return false;
        }

        try {
            config = ConfigMigration.MigrateIfNeeded(json).Config;
            return true;
        } catch (JsonException) {
            config = new() { Profiles = new() { ["default"] = new() } };
            return false;
        }
    }

    static void Publish(string path, ProfileConfig config) {
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tmp,
            JsonSerializer.SerializeToUtf8Bytes(config, ProfileConfigJsonContextIndented.Default.ProfileConfig));
        File.Move(tmp, path, overwrite: true);
    }
}
