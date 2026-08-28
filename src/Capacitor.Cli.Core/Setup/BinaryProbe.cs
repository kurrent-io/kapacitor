namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// A search path, plus this OS's rules for what on it is launchable: the one place a command name
/// is resolved to something spawnable. Detection asks whether a vendor's CLI is there; the headless
/// runners ask for the concrete path to hand <c>ProcessStartInfo</c>.
///
/// <para>Resolution is required rather than convenient: <c>FileName</c> with
/// <c>UseShellExecute = false</c> goes through <c>CreateProcess</c>, which appends only
/// <c>.exe</c>, so a bare <c>"codex"</c>/<c>"claude"</c> never finds npm's <c>.cmd</c> shim.
/// Handing the resolved path over is enough; .NET escapes <c>.cmd</c>/<c>.bat</c> arguments itself,
/// so no <c>cmd.exe /c</c> wrapper — which would reopen an argument-injection hole.</para>
///
/// <para>The search path is parsed on construction and the raw <c>PATH</c>/<c>PATHEXT</c> strings
/// are never surfaced: which variables they came from, how they split and which candidates a
/// platform will run are this type's concern alone. A caller names where to look — nothing
/// more.</para>
/// </summary>
public sealed class BinaryProbe {
    static StringComparer PathIdentity => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>What Windows launches a bare name through when PATHEXT is unset or empty.</summary>
    static readonly IReadOnlyList<string> DefaultExtensions = [".EXE", ".CMD", ".BAT", ".COM"];

    readonly IReadOnlyList<string> _directories;
    readonly IReadOnlyList<string> _extensions;

    BinaryProbe(IEnumerable<string> directories) {
        // One stat per directory however many times the search path names it; Distinct keeps the
        // first occurrence, so search order survives. Unobservable in a result — with or without it
        // the same command resolves to the same path.
        _directories = [.. directories.Distinct(PathIdentity)];
        _extensions  = LaunchableExtensions();
    }

    /// <summary>
    /// A probe over <paramref name="searchPath"/>, a <c>PATH</c>-shaped value: its directories are
    /// searched in order, so the first one holding a command wins. Null or empty finds nothing. The
    /// launch rules stay this host's — a caller names where to look, never what counts as runnable.
    /// </summary>
    public static BinaryProbe Searching(string? searchPath) =>
        new((searchPath ?? "").Split(Path.PathSeparator).Where(dir => !string.IsNullOrEmpty(dir)));

    /// <summary>The current process's own search path.</summary>
    public static BinaryProbe FromEnvironment() => Searching(Environment.GetEnvironmentVariable("PATH"));

    /// <summary>Shorthand for the current process: is <paramref name="command"/> launchable?</summary>
    public static bool OnPath(string? command) => FromEnvironment().Finds(command);

    /// <summary>Whether <paramref name="command"/> resolves to something executable.</summary>
    public bool Finds(string? command) => Resolve(command) is not null;

    /// <summary>
    /// Full path to the executable <paramref name="command"/> names, or null when nothing matches.
    /// A command carrying a directory component is a path rather than a search — as a shell treats
    /// <c>./codex</c> and <c>C:\tools\codex.cmd</c> alike — and is still retried with the launchable
    /// extensions, so an extensionless configured path resolves to its <c>.cmd</c>.
    /// </summary>
    public string? Resolve(string? command) {
        if (string.IsNullOrWhiteSpace(command)) return null;

        if (Path.IsPathRooted(command) || command.Contains('/') || command.Contains('\\'))
            return FirstCandidate(command);

        foreach (var dir in _directories) {
            string candidate;

            // A malformed entry (invalid characters on Windows) must not abort the walk.
            try {
                candidate = Path.Combine(dir, command);
            } catch (ArgumentException) {
                continue;
            }

            if (FirstCandidate(candidate) is { } hit) return hit;
        }

        return null;
    }

    /// <summary>
    /// <paramref name="basePath"/> resolved to something the OS will launch. On Unix that is the
    /// path itself when it carries an execute bit. On Windows the PATHEXT candidates go FIRST: npm
    /// drops an extensionless <c>#!/bin/sh</c> shim beside <c>codex.cmd</c>, and handing that script
    /// to <c>CreateProcess</c> fails with error 193, so the <c>.cmd</c> must win — and that shim is
    /// also why the fallback there admits only a path already carrying a launchable extension.
    /// </summary>
    string? FirstCandidate(string basePath) {
        foreach (var ext in _extensions) {
            if (IsExecutable(basePath + ext)) {
                return Full(basePath + ext);
            }
        }

        return LaunchableAsIs(basePath) && IsExecutable(basePath) ? Full(basePath) : null;
    }

    /// <summary>Whether the path can be launched as it stands. Unix asks the execute bit and nothing
    /// else; Windows launches through an extension, so an extensionless file — or one whose
    /// extension PATHEXT does not list — is unspawnable however executable it looks.</summary>
    bool LaunchableAsIs(string path) =>
        _extensions.Count == 0
     || _extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Absolute form, or the path unchanged when it cannot be expanded — a hit the caller
    /// can stat beats a null.</summary>
    static string Full(string path) {
        try {
            return Path.GetFullPath(path);
        } catch {
            return path;
        }
    }

    /// <summary>The extensions a bare name may be launched through — Windows' PATHEXT, and nothing
    /// on Unix, where the execute bit is the only rule. Read per probe rather than held in a static,
    /// which would answer from a value frozen at type init.</summary>
    static IReadOnlyList<string> LaunchableExtensions() {
        if (!OperatingSystem.IsWindows()) return [];

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");

        return string.IsNullOrEmpty(pathExt)
            ? DefaultExtensions
            : pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// File exists AND (on Unix) carries at least one of the user/group/other execute bits. True
    /// <c>access(X_OK)</c> would need a P/Invoke against the effective UID/GID; the rare false
    /// positive (execute bits set but an unrelated owner) degrades to the same outcome as a
    /// runtime-broken binary — a launch failure already surfaced.
    /// </summary>
    static bool IsExecutable(string path) {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true; // the extensions already filtered the candidates

        const UnixFileMode anyExecute =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        try {
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        } catch {
            // TOCTOU race (removed between the two calls), permission denied, or other I/O failure
            // — treat as not executable rather than advertising something unspawnable.
            return false;
        }
    }
}
