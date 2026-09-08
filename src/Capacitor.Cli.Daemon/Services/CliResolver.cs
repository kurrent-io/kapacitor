using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Best-effort lookup that mirrors how a shell finds an executable, over
/// <paramref name="binaries"/>'s own search path — a probe this type was handed at construction,
/// not one re-read from an ambient registry. The PATH/PATHEXT walk itself lives once, in
/// <see cref="BinaryProbe"/>; a second copy is how the Windows <c>.cmd</c> gap survived, so this
/// must never grow its own. Used by <see cref="IHostedAgentLauncher.IsAvailable"/> at daemon startup
/// to decide which vendor launchers to advertise over <c>DaemonConnect</c>.
///
/// <para>This intentionally does NOT execute the binary — startup must stay cheap. False
/// positives (binary present, exec bit set, but unusable for some other reason) surface
/// later as the existing <c>LaunchFailed</c> path.</para>
/// </summary>
internal sealed class CliResolver(BinaryProbe binaries) {
    /// <summary>
    /// Returns <c>true</c> when <paramref name="cliPath"/> resolves to an existing,
    /// executable file — either directly (rooted path) or via a search-path lookup (bare
    /// command).
    /// </summary>
    public bool Exists(string cliPath) => binaries.Finds(cliPath);

    /// <summary>
    /// The fully-qualified executable <paramref name="cliPath"/> resolves to, or null.
    /// </summary>
    public string? ResolveExecutable(string cliPath) => binaries.Resolve(cliPath);
}
