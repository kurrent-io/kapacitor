namespace kapacitor;

static class ClaudePaths {
    static readonly string Home = Path.Combine(PathHelpers.HomeDirectory, ".claude");

    public static string Projects     { get; } = Path.Combine(Home, "projects");
    public static string Plans        { get; } = Path.Combine(Home, "plans");
    public static string UserSettings { get; } = Path.Combine(Home, "settings.json");

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
