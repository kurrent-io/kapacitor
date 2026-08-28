using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Process-state seam for <see cref="PluginCommand"/>. Captures the values that
/// <c>kcap plugin install/remove</c> would otherwise read from
/// <see cref="Environment"/> / <see cref="Console"/>, so tests can supply
/// fakes without mutating shared process state.
///
/// <see cref="ResolvePluginPath"/> is a delegate (not a string) so the
/// filesystem probing in <see cref="SetupCommand.ResolvePluginPath(string?)"/>
/// only runs on the install branches that actually need it — not on
/// <c>remove</c>, <c>--cursor</c>, or early-exit invocations.
/// </summary>
public sealed record PluginEnvironment(
    UserHome       Home,
    // The resolved snapshot, not a ConfigRoot: the only reader wants EVERY profile's server_url
    // (the Codex sandbox allowlist covers all of them), never "which profile applies" — and the
    // process already resolved it, so a root here would only buy a second read of the same file.
    ProfileConfig  Profiles,
    Func<string?>  ResolvePluginPath,
    TextWriter     Stdout,
    TextWriter     Stderr
) {
    /// <summary>
    /// Resolves the native binary path written as the <c>command</c> of generated MCP
    /// registrations. Null → the production default (<see cref="Environment.ProcessPath"/>, see
    /// <c>KcapBinaryCommand</c>). Tests inject a deterministic path so assertions never bless
    /// whatever executable happens to be running the test.
    /// </summary>
    public Func<string?>? ResolveMcpBinaryPath { get; init; }

    /// <summary>
    /// Finds agent sessions already running at first install. A seam for the same reason the rest of
    /// this type is one: the default reads the machine's real process table, which a test must not.
    /// </summary>
    public Func<IEnumerable<StaleAgentTarget>, IReadOnlyList<StaleAgentProcess>> FindStaleAgents {
        get; init;
    } = StaleAgentProbe.Find;

    /// <summary>
    /// Every vendor's layout. Supplied rather than resolved here: this type is the process-state
    /// seam, so reading nine override variables to build itself would put back the ambient read it
    /// exists to remove. Held as one instance, so two members of one vendor cannot name different
    /// roots however the environment moves afterwards.
    /// </summary>
    public required HarnessPaths Paths { get; init; }

    public static PluginEnvironment FromProcess(ProfileConfig profiles, UserHome home) => new(
        Home:              home,
        Profiles:          profiles,
        ResolvePluginPath: () => SetupCommand.ResolvePluginPath(),
        Stdout:            Console.Out,
        Stderr:            Console.Error
    ) { Paths = HarnessPaths.FromEnvironment(home) };
}
