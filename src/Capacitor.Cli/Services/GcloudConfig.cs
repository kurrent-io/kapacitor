namespace Capacitor.Cli.Services;

/// <summary>
/// Reads gcloud's own on-disk configuration for the active default project — no process spawn, no
/// network. Used only by the service-install capture (<see cref="ServiceEnvironment.Capture"/>);
/// the daemon itself never reads this.
/// </summary>
static class GcloudConfig {
    /// <summary>The active configuration's <c>[core] project</c>, or null when gcloud is not set
    /// up, the config is unreadable, or no project is set. Never throws.</summary>
    public static string? DefaultProject(Core.UserHome home) {
        try {
            var root   = Path.Combine(home.Path, ".config", "gcloud");
            var active = "default";

            var activePath = Path.Combine(root, "active_config");
            if (File.Exists(activePath) && File.ReadAllText(activePath).Trim() is { Length: > 0 } name)
                active = name;

            var configPath = Path.Combine(root, "configurations", $"config_{active}");

            return File.Exists(configPath) ? ParseProject(File.ReadAllText(configPath)) : null;
        } catch {
            return null;
        }
    }

    /// <summary>Minimal INI walk: the <c>project</c> key inside the <c>[core]</c> section and
    /// nowhere else — the same key under another section (e.g. <c>[compute]</c>) means something
    /// different to gcloud and must not be read as the default project.
    ///
    /// <para>Two keys in one section is a file gcloud's own reader refuses, so this refuses it too
    /// rather than picking a winner it cannot know: a wrong project is baked into a unit and surfaces
    /// much later as an auth failure, where deriving nothing surfaces immediately as a partial trio.
    /// Whole-line comments, trailing comments and a quoted value are all stripped — hand-edited files
    /// carry all three, and each would otherwise be baked into the value verbatim.</para></summary>
    internal static string? ParseProject(string ini) {
        var inCore = false;
        string? found = null;

        foreach (var raw in ini.Split('\n')) {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] is '#' or ';') continue;

            if (line.StartsWith('[')) {
                inCore = line.Equals("[core]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inCore) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0 || !line[..eq].Trim().Equals("project", StringComparison.OrdinalIgnoreCase)) continue;

            if (found is not null) return null;

            found = CleanValue(line[(eq + 1)..]);
        }

        return found;
    }

    /// <summary>A trailing <c>#</c>/<c>;</c> comment removed, then one matching pair of surrounding
    /// quotes. Null for an empty result, which is the same as the key being absent.</summary>
    static string? CleanValue(string raw) {
        var cut   = raw.IndexOfAny(['#', ';']);
        var value = (cut >= 0 ? raw[..cut] : raw).Trim();

        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'')
            value = value[1..^1].Trim();

        return value.Length > 0 ? value : null;
    }
}
