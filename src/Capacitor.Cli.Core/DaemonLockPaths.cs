using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

/// <summary>
/// Path layout for per-name daemon lock + PID + start-lock files.
///
/// <para>The previous layout used singletons at <c>PathHelpers.ConfigPath("agent.pid")</c>
/// and <c>PathHelpers.ConfigPath("agent.start.lock")</c>. Two daemons with
/// different <c>KCAP_CONFIG_DIR</c>s (e.g. an Aspire-spawned dev daemon
/// using <c>.dev/kcap</c> alongside a user-launched daemon using
/// <c>~/.config/kcap</c>) wrote to different singletons and never saw
/// each other, allowing two daemons under the same name to authenticate as
/// the same GitHub ID and oscillate the server-side <c>DaemonRegistry</c>
/// slot. The staging incident that motivated this fix was exactly that.</para>
///
/// <para>This helper uses a <b>fixed location</b> under the home directory
/// (<c>~/.config/kcap/daemons/</c>) regardless of <c>KCAP_CONFIG_DIR</c>,
/// so cross-config-dir daemons under the same name now collide on the same
/// <c>flock</c>. Sanitization is intentionally strict to keep filenames
/// portable: only <c>[a-z0-9._-]</c> survives, anything else maps to <c>-</c>.
/// The one exception is the opt-in <see cref="DaemonsDirEnvVar"/> test seam
/// (never set in production) — see its remarks.</para>
/// </summary>
public static partial class DaemonLockPaths {
    /// <summary>
    /// Opt-in override for the DEFAULT daemons directory, read lazily on every access so a test
    /// assembly can redirect the whole process to a temp path before any daemon file is touched.
    /// Production and the Aspire dev daemon never set this, so the deliberate cross-<c>KCAP_CONFIG_DIR</c>
    /// collision on <c>~/.config/kcap/daemons/</c> is unchanged. It exists because this directory
    /// ignores <c>KCAP_CONFIG_DIR</c> and the default daemon name is the OS username: a unit test
    /// that reads this dir with <see cref="_overrideDir"/> cleared could otherwise resolve — and
    /// <c>Process.Kill</c> — the developer's live daemon (its pid file lives here).
    /// </summary>
    public const string DaemonsDirEnvVar = "KCAP_DAEMONS_DIR";

    /// <summary>
    /// Pure default-directory resolution: the supplied <see cref="DaemonsDirEnvVar"/> value when
    /// non-empty, else the fixed <c>~/.config/kcap/daemons/</c> home location. Kept env-free so the
    /// production fallback can be asserted with an explicit argument (see the test) without mutating
    /// the process-global environment variable — clearing it at runtime would race any parallel test
    /// that reads <see cref="Directory"/> and re-expose the real daemons dir this seam exists to hide.
    /// </summary>
    internal static string ResolveDefaultDir(string? envValue) =>
        !string.IsNullOrEmpty(envValue)
            ? envValue
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "kcap", "daemons"
            );

    static string DefaultDirectory => ResolveDefaultDir(Environment.GetEnvironmentVariable(DaemonsDirEnvVar));

    static string? _overrideDir;

    /// <summary>Directory where all per-name lock/pid/start files live.</summary>
    public static string Directory => _overrideDir ?? DefaultDirectory;

    /// <summary>
    /// Test-only override: redirects the daemons directory to a temp path.
    /// Pass <c>null</c> to restore the default home-directory location.
    /// Production code never sets this; it's exposed via
    /// <c>InternalsVisibleTo</c> for the test projects only.
    /// </summary>
    internal static void OverrideDirectoryForTesting(string? path) => _overrideDir = path;

    [GeneratedRegex(@"[^a-z0-9._-]")]
    private static partial Regex DisallowedChars();

    /// <summary>
    /// Lowercase the input, replace any character outside <c>[a-z0-9._-]</c>
    /// with <c>-</c>, collapse consecutive separators, and strip leading /
    /// trailing separators. Empty / whitespace-only input falls back to
    /// <c>"daemon"</c> so we never produce a file at the directory root.
    /// </summary>
    public static string Sanitize(string name) {
        if (string.IsNullOrWhiteSpace(name)) return "daemon";

        var lowered    = name.Trim().ToLowerInvariant();
        var normalized = DisallowedChars().Replace(lowered, "-");
        var collapsed  = string.Join('-', normalized.Split('-', StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length == 0 ? "daemon" : collapsed;
    }

    /// <summary>Path to the daemon-held flock file. Content = instance_id GUID.</summary>
    public static string LockPath(string daemonName) =>
        Path.Combine(Directory, $"{Sanitize(daemonName)}.lock");

    /// <summary>Path to the PID file. Content = PID + optional StartTicks line.</summary>
    public static string PidPath(string daemonName) =>
        Path.Combine(Directory, $"{Sanitize(daemonName)}.pid");

    /// <summary>
    /// Path to the CLI-side start lock — the brief critical-section lock the
    /// <c>kcap daemon start</c> supervisor takes around its
    /// check-spawn-write-PID sequence. Distinct from <see cref="LockPath"/>,
    /// which the daemon itself holds for its entire lifetime.
    /// </summary>
    public static string StartLockPath(string daemonName) =>
        Path.Combine(Directory, $"{Sanitize(daemonName)}.start");

    /// <summary>Path to the daemon's "restart pending" marker (queued restart-after-update state).</summary>
    public static string RestartPendingPath(string daemonName) =>
        Path.Combine(Directory, $"{Sanitize(daemonName)}.restart-pending");

    /// <summary>
    /// Path to the daemon's version marker — a freely-readable file (unlike the
    /// exclusively-flocked <see cref="LockPath"/>) holding the running daemon's
    /// version so <c>kcap daemon status</c> can report it without a socket
    /// round-trip. Same on-disk-marker pattern as <see cref="RestartPendingPath"/>.
    /// </summary>
    public static string VersionPath(string daemonName) =>
        Path.Combine(Directory, $"{Sanitize(daemonName)}.version");

    /// <summary>Ensures the parent directory exists. Safe to call repeatedly.</summary>
    public static void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);

    /// <summary>
    /// Returns the daemon names visible on disk — the union of names derived from the
    /// STATE markers <c>*.pid</c>, <c>*.restart-pending</c>, and <c>*.version</c>. Used
    /// by <c>daemon doctor</c> to classify held vs stale entries; covers orphan PID
    /// files (e.g. a stop that removed the lock but left the PID behind) and marker-only
    /// leftovers (a crash between queueing a restart and applying it, or a version
    /// marker left after an unclean exit).
    ///
    /// <para>A lone <c>*.lock</c> is deliberately NOT a name source. The lock file is a
    /// per-inode <c>flock</c> mutex, not a liveness marker: its mere presence never means
    /// a daemon exists (a live daemon also writes <c>.pid</c> inside the flock during
    /// startup, so it is always covered by the PID lane), and it cannot be safely deleted
    /// — unlinking a held/free <c>flock</c> file lets a later <c>daemon start</c> create a
    /// fresh inode at the same path and hold a SECOND independent flock, so <c>doctor
    /// --clean</c> deliberately leaves it. If a lone lock counted as a name, a cleaned
    /// entry (markers removed, inert lock left) would be re-listed forever — the exact bug
    /// this exclusion fixes. Once its markers are gone the name simply stops appearing;
    /// the inert lock lingers harmlessly and is reused by the next start of that name.</para>
    /// </summary>
    public static IReadOnlyList<string> EnumerateNames() {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var fromPids  = System.IO.Directory.EnumerateFiles(Directory, "*.pid")
            .Select(Path.GetFileNameWithoutExtension);
        var fromMarkers = System.IO.Directory.EnumerateFiles(Directory, "*.restart-pending")
            .Select(Path.GetFileNameWithoutExtension);
        var fromVersions = System.IO.Directory.EnumerateFiles(Directory, "*.version")
            .Select(Path.GetFileNameWithoutExtension);

        return [
            .. fromPids.Concat(fromMarkers).Concat(fromVersions)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .Distinct()
                .Order()
        ];
    }
}
