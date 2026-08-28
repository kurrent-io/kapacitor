namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>
/// Filesystem layout for Claude Code state. CLAUDE_CONFIG_DIR (when set) replaces
/// <c>~/.claude</c> wholesale — settings.json, projects/ and plans/ all move under it.
/// </summary>
public sealed class ClaudePaths {
    /// <summary>
    /// <paramref name="configDir"/> relocates <see cref="Home"/>, and with it every derived
    /// member. <see cref="UserConfigJson"/> is the exception: its base is the user home unless
    /// the config dir moves it, which is why both are resolved here rather than derived from
    /// <see cref="Home"/>.
    /// </summary>
    public ClaudePaths(UserHome home, string? configDir) {
        var root = !string.IsNullOrWhiteSpace(configDir) ? configDir : null;

        Home           = root ?? Path.Combine(home.Path, ".claude");
        UserConfigJson = Path.Combine(root ?? home.Path, ".claude.json");
        UserSkillsDir  = Path.Combine(home.Path, ".claude", "skills");
    }

    /// <summary>Reads the one override Claude honours; the home comes from the caller.</summary>
    public static ClaudePaths FromEnvironment(UserHome home) =>
        new(home, Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));

    public string Home { get; }

    /// <summary>The skills tree kcap installs into. Anchored on the user home rather than on
    /// <see cref="Home"/>, so a config-dir override does NOT move it — the only member here that
    /// does not follow the override.</summary>
    public string UserSkillsDir { get; }

    public string Projects     => Path.Combine(Home, "projects");
    public string Plans        => Path.Combine(Home, "plans");
    public string UserSettings => Path.Combine(Home, "settings.json");

    /// <summary>
    /// Claude's user-global config FILE (account/OAuth, MCP servers, per-project trust flags
    /// under <c>projects[path]</c>). With CLAUDE_CONFIG_DIR set it lives INSIDE the config dir;
    /// by default it is a SIBLING of <c>~/.claude</c>, not a child. Verified against Claude Code
    /// 2.1.196 — do NOT collapse this into <c>Path.Combine(Home, …)</c>.
    /// </summary>
    public string UserConfigJson { get; }

    /// <summary>
    /// Returns the project directory for a given repo path.
    /// Claude uses the absolute path with directory separators replaced by dashes.
    /// </summary>
    public string ProjectDir(string repoAbsolutePath) =>
        Path.Combine(Projects, PathToHash(repoAbsolutePath));

    static string PathToHash(string absolutePath) {
        var hash = absolutePath.Replace(Path.DirectorySeparatorChar, '-');

        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            hash = hash.Replace(Path.AltDirectorySeparatorChar, '-');

        // Claude Code replaces dots with dashes in project dir names. Without this, the daemon's
        // symlink lands at the wrong path and Claude creates a fresh project dir without MCP configs.
        hash = hash.Replace('.', '-');

        // Windows drive designator (e.g. "C:") is invalid in directory names
        return hash.Replace(':', '-');
    }
}
