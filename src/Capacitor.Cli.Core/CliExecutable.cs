namespace Capacitor.Cli.Core;

/// <summary>
/// Resolves a CLI command (<c>"codex"</c>, <c>"claude"</c>, or a configured path) to a
/// concrete executable the way a shell would: direct check for a rooted path, otherwise a
/// <c>PATH</c> walk, plus <c>PATHEXT</c> candidates on Windows. On Unix a candidate must
/// carry at least one execute bit.
///
/// <para>Resolution is required — not a nicety — for
/// <see cref="System.Diagnostics.ProcessStartInfo.FileName"/> with
/// <c>UseShellExecute = false</c>: that path goes through <c>CreateProcess</c>, which
/// appends only <c>.exe</c>. npm installs the agent CLIs on Windows as a <c>.cmd</c> shim
/// with no <c>.exe</c> alongside, so a bare <c>"codex"</c>/<c>"claude"</c> fails outright
/// with "The system cannot find the file specified" — which is how headless title and
/// what's-done generation silently degraded on Windows.</para>
///
/// <para>Handing the resolved full path to <c>FileName</c> is sufficient: .NET 8+ applies
/// cmd-specific argument escaping when the target is a <c>.cmd</c>/<c>.bat</c>, so a
/// <c>cmd.exe /c</c> wrapper is NOT needed here — and adding one would reintroduce the
/// argument-injection hazard that escaping fix closed.</para>
/// </summary>
public static class CliExecutable {
    /// <summary>
    /// Returns the full path to the executable <paramref name="command"/> names, or null
    /// when nothing matches. A rooted <paramref name="command"/> is checked directly (and,
    /// on Windows, retried with <c>PATHEXT</c> extensions so an extensionless configured
    /// path like <c>C:\tools\codex</c> still resolves to <c>codex.cmd</c>).
    /// </summary>
    public static string? Resolve(string? command) {
        if (string.IsNullOrWhiteSpace(command)) return null;

        // Anything with a directory component is a path, not a PATH lookup — mirrors how a
        // shell treats "./codex" and "C:\tools\codex.cmd" alike.
        if (Path.IsPathRooted(command) || command.Contains('/') || command.Contains('\\'))
            return FirstCandidate(command);

        var pathEnv = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(pathEnv)) return null;

        // Splitting on the platform separator is sufficient: POSIX disallows quoted PATH
        // entries and Windows tolerates raw paths in PATH.
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
            // A malformed PATH entry (invalid characters on Windows) must not abort the walk.
            string candidate;

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
    /// Whether <paramref name="command"/> resolves to something executable. Equivalent to
    /// <c>Resolve(command) is not null</c>; kept as a named predicate for the daemon's
    /// startup vendor probe.
    /// </summary>
    public static bool Exists(string? command) => Resolve(command) is not null;

    /// <summary>
    /// <paramref name="basePath"/> resolved to something Windows/Unix will actually launch.
    /// On Unix that is the path itself when it carries an execute bit. On Windows a bare
    /// extensionless file is NOT runnable via <c>CreateProcess</c> — npm installs a Git-Bash
    /// <c>#!/bin/sh</c> shim (no extension) right beside <c>codex.cmd</c>, and handing that
    /// script to <c>CreateProcess</c> fails with error 193 — so the base path wins only when
    /// it already carries a <c>PATHEXT</c> extension; otherwise the first <c>PATHEXT</c>
    /// candidate appended to it does. Hits come back fully qualified.
    /// </summary>
    static string? FirstCandidate(string basePath) {
        if (!OperatingSystem.IsWindows())
            return IsExecutable(basePath) ? Full(basePath) : null;

        if (HasExecutableExtension(basePath) && File.Exists(basePath)) return Full(basePath);

        foreach (var ext in WindowsExtensions()) {
            var candidate = basePath + ext;

            if (File.Exists(candidate)) return Full(candidate);
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/>'s extension is one <c>PATHEXT</c> lists — the
    /// only files Windows launches by name. A bare extensionless twin never qualifies.</summary>
    static bool HasExecutableExtension(string path) {
        var ext = Path.GetExtension(path);

        if (ext.Length == 0) return false;

        foreach (var known in WindowsExtensions())
            if (string.Equals(known, ext, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>Absolute form, or the path unchanged if it can't be expanded (a hit we could
    /// stat is more useful to the caller than a null).</summary>
    static string? Full(string path) {
        try {
            return Path.GetFullPath(path);
        } catch {
            return path;
        }
    }

    static string[] WindowsExtensions() {
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var raw     = string.IsNullOrEmpty(pathExt) ? ".EXE;.CMD;.BAT;.COM" : pathExt;

        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// File exists AND (on Unix) has at least one execute bit set. True <c>access(X_OK)</c>
    /// would need a P/Invoke against the effective UID/GID; the rare false positive
    /// (execute bits set but unrelated owner) degrades to the same outcome as a
    /// runtime-broken binary — a launch failure we already surface.
    /// </summary>
    static bool IsExecutable(string path) {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true; // PATHEXT already filtered the candidates

        const UnixFileMode anyExecute =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        try {
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        } catch {
            // TOCTOU race (file removed between the two calls), permission denied, or other
            // I/O failure — treat as not executable so we never advertise a vendor we can't
            // actually spawn.
            return false;
        }
    }
}
