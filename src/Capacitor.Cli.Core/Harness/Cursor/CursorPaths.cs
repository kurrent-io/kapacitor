namespace Capacitor.Cli.Core.Harness.Cursor;

public enum OsPlatform { MacOs, Linux, Windows }

public sealed record CursorPaths(string UserDir, string WorkspaceStorageDir) {
    public static CursorPaths Resolve(string? home = null, OsPlatform? platform = null, string? appData = null) {
        home     ??= PathHelpers.HomeDirectory;
        platform ??= OperatingSystem.IsMacOS()   ? OsPlatform.MacOs
                  :  OperatingSystem.IsWindows() ? OsPlatform.Windows
                  :                                OsPlatform.Linux;

        var sep = platform == OsPlatform.Windows ? '\\' : '/';

        var userDir = platform switch {
            OsPlatform.MacOs   => Join(sep, home, "Library", "Application Support", "Cursor", "User"),
            OsPlatform.Windows => Join(sep, appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User"),
            _                  => Join(sep, home, ".config", "Cursor", "User")
        };
        return new CursorPaths(
            UserDir:             userDir,
            WorkspaceStorageDir: userDir + sep + "workspaceStorage");
    }

    static string Join(char sep, string root, params string[] parts)
        => root.TrimEnd(sep) + sep + string.Join(sep, parts);

    /// <summary>
    /// True when any of the OS-specific Cursor user dirs exists. Detection by
    /// directory presence — Cursor IDE users without the <c>cursor</c> shell
    /// command on PATH must still be detected (design, Q7).
    /// </summary>
    public static bool IsInstalled(string? home = null, OsPlatform? platform = null, string? appData = null) {
        home     ??= PathHelpers.HomeDirectory;
        platform ??= OperatingSystem.IsMacOS()   ? OsPlatform.MacOs
                  :  OperatingSystem.IsWindows() ? OsPlatform.Windows
                  :                                OsPlatform.Linux;
        appData  ??= OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : null;

        return IsInstalledPure(home, platform.Value, appData);
    }

    /// <summary>Pure variant of <see cref="IsInstalled"/> for fully-injected callers (e.g.
    /// <see cref="Setup.AgentDetection"/>) — <paramref name="platform"/>/<paramref name="appData"/>
    /// are concrete inputs, never resolved via <see cref="OperatingSystem"/> or
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> internally.</summary>
    public static bool IsInstalledPure(string home, OsPlatform platform, string? appData) {
        // Universal: ~/.cursor/ (settings + hooks.json land here on every OS).
        if (Directory.Exists(Path.Combine(home, ".cursor"))) return true;

        // Per-OS Electron user dir.
        var perOs = platform switch {
            OsPlatform.MacOs   => Path.Combine(home, "Library", "Application Support", "Cursor", "User"),
            OsPlatform.Windows => appData is null ? null : Path.Combine(appData, "Cursor", "User"),
            _                  => Path.Combine(home, ".config", "Cursor", "User")
        };
        return perOs is not null && Directory.Exists(perOs);
    }

    /// <summary>Path to <c>~/.cursor/hooks.json</c> — same on every OS.</summary>
    public static string UserHooksJson(string? home = null) {
        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".cursor", "hooks.json");
    }

    /// <summary>Path to <c>~/.cursor/mcp.json</c> — same on every OS.</summary>
    public static string UserMcpJson(string? home = null) {
        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".cursor", "mcp.json");
    }

    /// <summary>Hook-event spool directory at <c>~/.cursor/kcap-pending/</c>.</summary>
    public static string SpoolDir(string? home = null) {
        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".cursor", "kcap-pending");
    }

    /// <summary>
    /// Per-session JSONL transcript root at <c>~/.cursor/projects/</c>. Each
    /// session lives at <c>&lt;projectsDir&gt;/&lt;sanitized-workspace&gt;/agent-transcripts/&lt;session-id&gt;/&lt;session-id&gt;.jsonl</c>
    /// in Anthropic content-block format. Same on every OS — Cursor uses the
    /// user's home dir, not the per-platform Electron user dir.
    /// </summary>
    public static string ProjectsDir(string? home = null) {
        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".cursor", "projects");
    }
}
