namespace Capacitor.Cli.Core.Harness.Claude;

static class ClaudePaths {
    // Lazy: HOME may be mutated at runtime (tests inject a fake home), so
    // these must re-evaluate on every access, the same way AgentsPaths does.
    // A static-readonly initializer would bake in HOME at first touch and
    // ignore subsequent changes. CLAUDE_CONFIG_DIR (when set) replaces ~/.claude
    // wholesale — settings.json, projects/, plans/ all move under it.
    public static string Home(string? home = null, string? configDir = null) {
        configDir ??= Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir)) return configDir;

        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".claude");
    }

    public static string Projects     => Path.Combine(Home(), "projects");
    public static string Plans        => Path.Combine(Home(), "plans");
    public static string UserSettings => Path.Combine(Home(), "settings.json");

    /// <summary>
    /// Path to Claude's user-global config FILE (account/OAuth, MCP servers,
    /// per-project trust flags under <c>projects[path]</c>). Its base differs
    /// from <see cref="Home"/>: with CLAUDE_CONFIG_DIR set it lives INSIDE the
    /// config dir (<c>$CLAUDE_CONFIG_DIR/.claude.json</c>); by default it is a
    /// SIBLING of <c>~/.claude</c> (<c>$HOME/.claude.json</c>). Verified against
    /// Claude Code 2.1.196 — do NOT collapse this into Path.Combine(Home(), …).
    /// </summary>
    public static string UserConfigJson(string? home = null, string? configDir = null) {
        configDir ??= Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir)) return Path.Combine(configDir, ".claude.json");

        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".claude.json");
    }

    /// <summary>
    /// Returns the project directory for a given repo path.
    /// Claude uses the absolute path with directory separators replaced by dashes.
    /// </summary>
    public static string ProjectDir(string repoAbsolutePath) =>
        Path.Combine(Projects, PathToHash(repoAbsolutePath));

    static string PathToHash(string absolutePath) {
        var hash = absolutePath.Replace(Path.DirectorySeparatorChar, '-');

        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            hash = hash.Replace(Path.AltDirectorySeparatorChar, '-');

        // Claude Code replaces dots with dashes in project dir names.
        // Without this, the daemon's symlink lands at the wrong path and
        // Claude creates a fresh project dir without MCP configs.
        hash = hash.Replace('.', '-');

        // Windows drive designator (e.g. "C:") is invalid in directory names
        return hash.Replace(':', '-');
    }
}
