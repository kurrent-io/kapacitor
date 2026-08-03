namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The daemon's advertised local-control capability list, surfaced on <c>HelloReply</c>.
/// INVARIANT: an entry may exist here ONLY if <see cref="LocalControlServer"/> actually
/// routes the corresponding frame(s) to a real handler — this list is assembled right next
/// to that routing switch so a capability can never be advertised without behavior behind
/// it. Both entries' handlers are live in this build: <c>"consent/1"</c> routes
/// ConsentSubscribe/ConsentResolve/ConsentRulesGet/ConsentRulesPut to <see cref="LaunchConsentIpc"/>,
/// and <c>"status/1"</c> routes StatusSubscribe to <see cref="DaemonStatusIpc"/>.
/// </summary>
internal static class LocalControlCapabilities {
    public static readonly IReadOnlyList<string> Current = ["consent/1", "status/1"];
}
