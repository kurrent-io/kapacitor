namespace Capacitor.Cli.Core;

/// <summary>
/// Shared resolution of the daemon's name. Lives in Capacitor.Core
/// so the CLI supervisor and the daemon binary agree on which name the
/// per-name lock / PID files belong to. Precedence — first non-empty
/// source wins:
///
/// <list type="number">
/// <item><c>--name &lt;value&gt;</c> on the command line — the most
///     explicit signal, takes priority over everything else.</item>
/// <item><c>KCAP_DAEMON_NAME</c> environment variable — useful for
///     shell scripts that fan out multiple daemons without rewriting
///     argv (e.g. <c>direnv</c> per shell).</item>
/// <item><c>profile.daemon.name</c> from the active profile (resolved
///     via <c>AppConfig.ResolveActiveProfile</c>).</item>
/// <item>OS username, lowercased.</item>
/// <item>Machine name, lowercased.</item>
/// <item>The literal string <c>"daemon"</c>.</item>
/// </list>
///
/// <para>Note: <c>DaemonRunner.RunAsync</c> previously had the env var
/// unconditionally override <c>--name</c>. That was an unintentional
/// inversion of the usual CLI convention (explicit flag wins). This
/// fixes it so <c>--name</c> is the strongest signal as users expect.</para>
/// </summary>
public static class DaemonNameResolver {
    /// <summary>
    /// Parse the daemon name from <paramref name="args"/>. Pass the profile-supplied default —
    /// <c>ProfileContext.DaemonName</c>, which states which profile that reads. Returns the
    /// resolved name; never null or empty.
    /// </summary>
    public static string Resolve(string[] args, string? profileName = null) {
        // --name is strict: if present, the next token must exist and not
        // look like another flag. Silently falling back to env/profile/OS
        // username when the user typed `--name` with no value would mask
        // typos and (combined with --yes in CLI command paths) could turn
        // `daemon stop --yes --name` into "stop every daemon I own". The
        // CLI catches this ArgumentException and surfaces it as a clean
        // non-zero exit; the daemon binary likewise refuses to start so
        // a bad systemd unit / npm script invocation fails loudly.
        for (var i = 0; i < args.Length; i++) {
            if (args[i] != "--name") continue;

            if (i + 1 >= args.Length || string.IsNullOrEmpty(args[i + 1]) || args[i + 1].StartsWith('-')) {
                var got = i + 1 < args.Length ? $"'{args[i + 1]}'" : "<end of args>";
                throw new ArgumentException(
                    $"--name requires a value (got {got}). " +
                    "Pass a value (e.g. --name laptop) or omit the flag entirely."
                );
            }

            return args[i + 1];
        }

        if (Environment.GetEnvironmentVariable("KCAP_DAEMON_NAME") is { Length: > 0 } envName)
            return envName;

        if (!string.IsNullOrEmpty(profileName))
            return profileName;

        var userName = Environment.UserName;
        if (!string.IsNullOrEmpty(userName)) return userName.ToLowerInvariant();

        var machine = Environment.MachineName;
        return !string.IsNullOrEmpty(machine) ? machine.ToLowerInvariant() : "daemon";
    }
}
