using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Best-effort lookup that mirrors how a shell finds an executable — a thin alias over
/// <see cref="BinaryProbe"/>, which owns the one copy of the PATH/PATHEXT walk. A second copy is
/// how the Windows <c>.cmd</c> gap survived, so this must never grow its own.
/// Used by <see cref="IHostedAgentLauncher.IsAvailable"/> at daemon startup to decide which
/// vendor launchers to advertise over <c>DaemonConnect</c>.
///
/// <para>This intentionally does NOT execute the binary — startup must stay cheap. False
/// positives (binary present, exec bit set, but unusable for some other reason) surface
/// later as the existing <c>LaunchFailed</c> path.</para>
/// </summary>
internal static class CliResolver {
    /// <summary>
    /// Returns <c>true</c> when <paramref name="cliPath"/> resolves to an existing,
    /// executable file — either directly (rooted path) or via <c>PATH</c> lookup (bare
    /// command).
    /// </summary>
    public static bool Exists(string cliPath) => BinaryProbe.OnPath(cliPath);

    /// <summary>
    /// The fully-qualified executable <paramref name="cliPath"/> resolves to, or null.
    /// </summary>
    public static string? ResolveExecutable(string cliPath) => BinaryProbe.FromEnvironment().Resolve(cliPath);
}
