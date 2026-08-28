namespace Capacitor.Cli.Core;

/// <summary>
/// Where this process keeps its kcap configuration, and the only thing that turns a filename into a
/// path under it. Passed explicitly to everything that needs it — there is no ambient default.
///
/// <para>Named members belong on the <b>owner</b> of each file (<c>AppConfig</c> owns
/// <c>config.json</c>, <c>MachineId</c> owns <c>machine.json</c>), never here: a root that
/// enumerated its tenants' filenames would have to change every time one of them gained a file.</para>
///
/// <para>Unrelated to <see cref="DaemonStore"/> despite the shared <c>~/.config/kcap</c> prefix —
/// that one is a fixed location and deliberately ignores <c>KCAP_CONFIG_DIR</c>.</para>
/// </summary>
public sealed class ConfigRoot(string directory) {
    /// <summary>Read only by <see cref="FromEnvironment"/>, at a process entry point. A transport for
    /// handing a directory to a child, not a source of truth — consulting it downstream would restore
    /// the process-global fallback this type exists to remove.</summary>
    public const string ConfigDirEnvVar = "KCAP_CONFIG_DIR";

    /// <summary>The configuration directory itself. Not guaranteed to exist.</summary>
    public string Directory { get; } = directory;

    /// <summary>
    /// The path to a file or subdirectory under the root. No segments returns <see cref="Directory"/>.
    /// </summary>
    // Join, not Combine: Combine("/root", "/etc", "passwd") discards the root and returns
    // "/etc/passwd". Join keeps it, textually — it normalises nothing, so ".." would still walk out.
    public string Path(params ReadOnlySpan<string> segments) =>
        System.IO.Path.Join([Directory, ..segments]);

    /// <summary>
    /// Cross-process lock on one file under the root, named by the same <paramref name="name"/> its
    /// owner passes to <see cref="Path"/>. Dispose to release; throws on timeout or a foreign-owned
    /// mutex. Because the lock's identity comes from the root, two roots never contend.
    /// </summary>
    public IDisposable AcquireLock(string name, TimeSpan? timeout = null) =>
        ConfigFileLock.Acquire(Path(name), timeout);

    /// <summary>The context for this process. Call once, in <c>Main</c> or the composition root.</summary>
    public static ConfigRoot FromEnvironment() {
        if (Environment.GetEnvironmentVariable(ConfigDirEnvVar) is { Length: > 0 } configured)
            return new(configured);

        return UnderHome(UserHome.FromEnvironment().Path);
    }

    /// <summary>The root a process with this home resolves for itself. The one place that knows the
    /// layout, so a spawner rewriting a child's <c>HOME</c> can name the same directory the child
    /// would have derived.</summary>
    public static ConfigRoot UnderHome(string home) =>
        new(System.IO.Path.Combine(home, ".config", "kcap"));
}
