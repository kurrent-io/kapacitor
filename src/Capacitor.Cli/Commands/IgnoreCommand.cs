using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

public sealed class IgnoreCommand(ConfigRoot root, ProfileContext profiles) {
    public async Task<int> HandleAsync(string[] args) {
        // args[0] == "ignore"; --help / -h is handled by the dispatcher in Program.cs.
        if (args.Length < 2) return Usage();

        switch (args[1]) {
            case "--list":
                return await List();
            case "--remove" when args.Length < 3:
                await Console.Error.WriteLineAsync("Usage: kcap ignore --remove <path>");

                return 1;
            case "--remove":
                return await Remove(args[2]);
            default:
                return await Add(args[1]);
        }
    }

    async Task<int> Add(string path) {
        if (!TryNormalize(path, out var normalized, out var error)) {
            await Console.Error.WriteLineAsync($"Invalid path '{path}': {error}");

            return 1;
        }

        var (_, profileName, profile) = LoadActive();
        var before = Current(profile).Length;

        profile = ApplyAdd(profile, normalized);

        await ConfigMutator.MutateAsync(root, c => {
            var p = ApplyAdd(c.Profiles.GetValueOrDefault(profileName) ?? new Profile(), normalized);

            return c with { Profiles = new Dictionary<string, Profile>(c.Profiles) { [profileName] = p } };
        });

        if (Current(profile).Length == before) {
            await Console.Out.WriteLineAsync($"Already ignored: {normalized} (profile: {profileName})");
        } else {
            await Console.Out.WriteLineAsync($"Ignoring: {normalized} (profile: {profileName})");
        }

        return 0;
    }

    async Task<int> Remove(string path) {
        if (!TryNormalize(path, out var normalized, out var error)) {
            await Console.Error.WriteLineAsync($"Invalid path '{path}': {error}");

            return 1;
        }

        var (_, profileName, profile) = LoadActive();
        var before = Current(profile).Length;

        profile = ApplyRemove(profile, normalized);

        await ConfigMutator.MutateAsync(root, c => {
            var p = ApplyRemove(c.Profiles.GetValueOrDefault(profileName) ?? new Profile(), normalized);

            return c with { Profiles = new Dictionary<string, Profile>(c.Profiles) { [profileName] = p } };
        });

        if (Current(profile).Length == before) {
            await Console.Out.WriteLineAsync($"Not in ignore list: {normalized} (profile: {profileName})");
        } else {
            await Console.Out.WriteLineAsync($"Removed: {normalized} (profile: {profileName})");
        }

        return 0;
    }

    static bool TryNormalize(string path, out string normalized, out string error) {
        try {
            var n = PathExclusion.Normalize(path);

            if (string.IsNullOrWhiteSpace(n)) {
                normalized = "";
                error      = "path is empty after normalization";

                return false;
            }

            normalized = n;
            error      = "";

            return true;
        } catch (Exception ex) {
            normalized = "";
            error      = ex.Message;

            return false;
        }
    }

    async Task<int> List() {
        var (_, profileName, profile) = LoadActive();
        var paths = Current(profile);

        if (paths.Length == 0) {
            await Console.Out.WriteLineAsync($"No ignored paths (profile: {profileName}).");

            return 0;
        }

        await Console.Out.WriteLineAsync($"Ignored paths (profile: {profileName}):");

        foreach (var p in paths)
            await Console.Out.WriteLineAsync($"  {p}");

        return 0;
    }

    /// <summary>
    /// Pure: returns a new <see cref="Profile"/> with <paramref name="path"/> added to
    /// <see cref="Profile.ExcludedPaths"/>, normalized and deduped. Per-entry
    /// normalization is guarded so a hand-edited entry that Normalize rejects
    /// (null byte, etc.) doesn't crash the command. Exposed for testing.
    /// </summary>
    public static Profile ApplyAdd(Profile profile, string path) {
        var normalized = PathExclusion.Normalize(path);
        var current    = Current(profile);

        if (current.Any(existing => SafeNormalize(existing) == normalized))
            return profile;

        return profile with { ExcludedPaths = [.. current, normalized] };
    }

    /// <summary>
    /// Pure: returns a new <see cref="Profile"/> with <paramref name="path"/> removed
    /// from <see cref="Profile.ExcludedPaths"/>. Per-entry normalization is guarded
    /// — non-normalizable entries are kept (skipped from the removal predicate) so a
    /// bad entry in the stored list doesn't crash the command. Exposed for testing.
    /// </summary>
    public static Profile ApplyRemove(Profile profile, string path) {
        var normalized = PathExclusion.Normalize(path);
        var current    = Current(profile);

        var remaining = current
            .Where(existing => SafeNormalize(existing) != normalized)
            .ToArray();

        return remaining.Length == current.Length
            ? profile
            : profile with { ExcludedPaths = remaining };
    }

    static string? SafeNormalize(string entry) {
        try { return PathExclusion.Normalize(entry); } catch { return null; }
    }

    // JSON source-gen for init-only array properties leaves the value null when
    // the JSON key is absent, even though the C# initializer is `= []`. Treat
    // null as empty everywhere that touches the array.
    static string[] Current(Profile profile) => profile.ExcludedPaths ?? [];

    // The startup snapshot, not a re-read: the write below goes through ConfigMutator, which
    // re-reads under its own lock anyway.
    (ProfileConfig Config, string ProfileName, Profile Profile) LoadActive() =>
        (profiles.Snapshot, profiles.Name, profiles.Effective ?? new Profile());

    static int Usage() {
        Console.Error.WriteLine("Usage: kcap ignore <path>");
        Console.Error.WriteLine("       kcap ignore --list");
        Console.Error.WriteLine("       kcap ignore --remove <path>");

        return 1;
    }
}
