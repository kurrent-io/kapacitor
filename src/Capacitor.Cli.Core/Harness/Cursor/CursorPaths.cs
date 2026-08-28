namespace Capacitor.Cli.Core.Harness.Cursor;

public enum OsPlatform { MacOs, Linux, Windows }

/// <summary>
/// Filesystem layout for Cursor. Two roots, not one: the universal <c>~/.cursor</c> (settings,
/// hooks.json, projects/ — same on every OS, under the user's home rather than the Electron user
/// dir) and the per-OS Electron user dir that holds <c>workspaceStorage</c>.
/// </summary>
public sealed class CursorPaths {
    readonly string _home;
    readonly string? _perOsUserDir;

    /// <param name="appData">Windows' Roaming AppData. Null means unset, which leaves the Electron
    /// user dir unresolvable — detection then rests on <c>~/.cursor</c> alone.</param>
    public CursorPaths(UserHome home, OsPlatform platform, string? appData) {
        _home = home.Path;

        // Separator from the INJECTED platform, not the host's, so a Windows layout composes with
        // backslashes even when resolved on a Mac.
        var sep = platform == OsPlatform.Windows ? '\\' : '/';

        UserDir = platform switch {
            OsPlatform.MacOs   => Join(sep, _home, "Library", "Application Support", "Cursor", "User"),
            OsPlatform.Windows => Join(sep, appData ?? "", "Cursor", "User"),
            _                  => Join(sep, _home, ".config", "Cursor", "User")
        };
        WorkspaceStorageDir = UserDir + sep + "workspaceStorage";

        _perOsUserDir = platform switch {
            OsPlatform.MacOs   => Path.Combine(_home, "Library", "Application Support", "Cursor", "User"),
            OsPlatform.Windows => appData is null ? null : Path.Combine(appData, "Cursor", "User"),
            _                  => Path.Combine(_home, ".config", "Cursor", "User")
        };
    }

    /// <summary>Current-process platform and AppData; the home comes from the caller.</summary>
#pragma warning disable RS0030 // Windows' AppData is not derivable from a home
    public static CursorPaths FromEnvironment(UserHome home) => new(
        home,
        OperatingSystem.IsMacOS()   ? OsPlatform.MacOs
      : OperatingSystem.IsWindows() ? OsPlatform.Windows
      :                               OsPlatform.Linux,
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : null);
#pragma warning restore RS0030

    /// <summary>The per-OS Electron user dir.</summary>
    public string UserDir { get; }

    public string WorkspaceStorageDir { get; }

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
    /// True when either Cursor root exists. Detection by directory presence — Cursor IDE users
    /// without the <c>cursor</c> shell command on PATH must still be detected.
    /// </summary>
    public bool IsInstalled =>
        Directory.Exists(CursorDir) || (_perOsUserDir is not null && Directory.Exists(_perOsUserDir));

    static string Join(char sep, string root, params string[] parts)
        => root.TrimEnd(sep) + sep + string.Join(sep, parts);
}
