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
    /// different to gcloud and must not be read as the default project.</summary>
    internal static string? ParseProject(string ini) {
        var inCore = false;

        foreach (var raw in ini.Split('\n')) {
            var line = raw.Trim();

            if (line.StartsWith('[')) {
                inCore = line.Equals("[core]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inCore) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0 || !line[..eq].Trim().Equals("project", StringComparison.OrdinalIgnoreCase)) continue;

            var value = line[(eq + 1)..].Trim();

            return value.Length > 0 ? value : null;
        }

        return null;
    }
}
