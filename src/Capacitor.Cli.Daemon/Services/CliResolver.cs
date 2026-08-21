using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Best-effort lookup that mirrors how a shell finds an executable. Now a thin alias over
/// <see cref="CliExecutable"/>, which owns the single copy of the PATH/PATHEXT walk shared
/// with the headless runners — this file used to carry its own near-identical copy behind a
/// "keep the two in sync" comment that the Windows <c>.cmd</c> gap slipped through anyway.
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
    public static bool Exists(string cliPath) => CliExecutable.Exists(cliPath);

    /// <summary>
    /// The fully-qualified executable <paramref name="cliPath"/> resolves to, or null.
    /// </summary>
    public static string? ResolveExecutable(string cliPath) => CliExecutable.Resolve(cliPath);
}
