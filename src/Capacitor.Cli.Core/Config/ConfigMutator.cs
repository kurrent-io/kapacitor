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
        // Attempt the read directly rather than probing Directory.Exists/File.Exists first: BOTH
        // return false on a permission-denied path (or an unreadable ancestor directory), which
        // would silently degrade "inaccessible" into "absent, defaults are fine" — exactly wrong
        // for a caller (the start gate) that must fail closed on unreadable evidence rather than
        // proceed as though nothing were configured.
        string json;
        try {
            json = File.ReadAllText(path);
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            // A link at the exact path (dangling included), or a file/link ancestor, is
            // structural evidence — unreadable, not absence.
            if (PathEvidence.PathBlockedByFileOrLink(path)) {
                config = FreshDefault();
                return false;
            }

            config = FreshDefault();
            return true;
        } catch {
            // A directory sitting at the config path (UnauthorizedAccessException on read), a
            // permission error, or any other I/O failure — unreadable EVIDENCE, never silently
            // folded into absence.
            config = FreshDefault();
            return false;
        }

        // Validate the document itself BEFORE handing it to migration: MigrateIfNeeded treats an
        // unparseable document, or one whose root isn't a JSON object, as v1-absent and silently
        // returns a fresh default — correct for LoadPure, wrong for this method's fail-closed
        // contract.
        try {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.IsObject) throw new JsonException("config root is not a JSON object");
        } catch (JsonException) {
            config = FreshDefault();
            return false;
        }

        try {
            config = ConfigMigration.MigrateIfNeeded(json).Config;
            return true;
        } catch (JsonException) {
            config = FreshDefault();
            return false;
        }
    }

    static ProfileConfig FreshDefault() => new() { Profiles = new() { ["default"] = new() } };

    static void Publish(string path, ProfileConfig config) {
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tmp,
            JsonSerializer.SerializeToUtf8Bytes(config, ProfileConfigJsonContextIndented.Default.ProfileConfig));
        // Windows denies replace-into-place while any reader holds the destination without
        // FILE_SHARE_DELETE; readers are short-lived, so retry briefly before surfacing.
        for (var attempt = 0; ; attempt++) {
            try {
                File.Move(tmp, path, overwrite: true);
                return;
            } catch (Exception e) when (e is UnauthorizedAccessException or IOException && attempt < 49) {
                Thread.Sleep(20);
            } catch {
                try { File.Delete(tmp); } catch { /* best effort */ }
                throw;
            }
        }
    }
}
