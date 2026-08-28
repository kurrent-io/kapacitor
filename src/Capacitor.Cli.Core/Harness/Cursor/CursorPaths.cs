namespace Capacitor.Cli.Core.Harness.Cursor;

/// <summary>
/// Filesystem layout for Cursor. Two roots, not one: the universal <c>~/.cursor</c> (settings,
/// hooks.json, projects/ — same on every OS, under the user's home rather than the Electron user
/// dir) and this host's Electron user dir, which holds <c>workspaceStorage</c>.
///
/// <para>Which Electron dir that is depends on the running OS, and this class is the only place that
/// asks: a layout for an OS this process is not running on is a shape nothing here can read files
/// through.</para>
/// </summary>
public sealed class CursorPaths {
    readonly string _home;
    readonly bool   _userDirIsNameable;

    public CursorPaths(UserHome home) {
        _home = home.Path;

        // Windows keeps the Electron dir under Roaming AppData, which no home derives; every other
        // host puts it under the home itself.
#pragma warning disable RS0030 // Roaming AppData is not derivable from a UserHome
        var appData = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : "";
#pragma warning restore RS0030

        UserDir = OperatingSystem.IsMacOS()   ? Path.Combine(_home, "Library", "Application Support", "Cursor", "User")
                : OperatingSystem.IsWindows() ? Path.Combine(appData, "Cursor", "User")
                :                               Path.Combine(_home, ".config", "Cursor", "User");

        // An AppData the OS declines to name leaves UserDir relative, and probing a relative path
        // reads the working directory instead. Whether it was named is knowable only here.
        _userDirIsNameable = !OperatingSystem.IsWindows() || appData.Length > 0;
    }

    /// <summary>This host's Electron user dir.</summary>
    public string UserDir { get; }

    public string WorkspaceStorageDir => Path.Combine(UserDir, "workspaceStorage");

    /// <summary>The universal <c>~/.cursor</c> root, on every OS.</summary>
    public string CursorDir => Path.Combine(_home, ".cursor");

    /// <summary>Path to <c>~/.cursor/hooks.json</c>.</summary>
    public string UserHooksJson => Path.Combine(CursorDir, "hooks.json");

    /// <summary>Path to <c>~/.cursor/mcp.json</c>.</summary>
    public string UserMcpJson => Path.Combine(CursorDir, "mcp.json");

    /// <summary>Hook-event spool directory at <c>~/.cursor/kcap-pending/</c>.</summary>
    public string SpoolDir => Path.Combine(CursorDir, "kcap-pending");

    /// <summary>
    /// Per-session JSONL transcript root at <c>~/.cursor/projects/</c>. Each
    /// session lives at <c>&lt;projectsDir&gt;/&lt;sanitized-workspace&gt;/agent-transcripts/&lt;session-id&gt;/&lt;session-id&gt;.jsonl</c>
    /// in Anthropic content-block format.
    /// </summary>
    public string ProjectsDir => Path.Combine(CursorDir, "projects");

    /// <summary>
    /// True when either Cursor root exists, asked now — an install that appears later must be seen.
    /// Detection by directory presence: Cursor IDE users without the <c>cursor</c> shell command on
    /// PATH must still be detected.
    ///
    /// <para>A root this host could not name is no signal at all, and detection then rests on
    /// <c>~/.cursor</c> alone.</para>
    /// </summary>
    public bool IsInstalled =>
        Directory.Exists(CursorDir) || (_userDirIsNameable && Directory.Exists(UserDir));
}
