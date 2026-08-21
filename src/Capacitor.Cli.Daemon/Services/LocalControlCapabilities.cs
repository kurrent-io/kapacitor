namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The daemon's advertised local-control capability list, surfaced on <c>HelloReply</c>.
/// INVARIANT: an entry may exist here ONLY if <see cref="LocalControlServer"/> actually
/// routes the corresponding frame(s) to a real handler — this list is assembled right next
/// to that routing switch so a capability can never be advertised without behavior behind
/// it. All four entries' handlers are live in this build: <c>"consent/1"</c> routes
/// ConsentSubscribe/ConsentResolve/ConsentRulesGet/ConsentRulesPut to <see cref="LaunchConsentIpc"/>;
/// <c>"consent/2"</c> routes ConsentSubscribeV2/ConsentResolveV2 to the same handler with
/// identity-checked resolution (mandatory <c>prompt_id</c> echo), <c>rule_saved</c> acks, and
/// <c>prompt_id</c>/<c>requester_display</c>-stamped pendings — this list entry is discovery
/// only, enforcement lives in the v2 frames themselves; <c>"consent/3"</c> routes
/// ConsentRulesPutV2 to the same handler, gating the policy replacement on an
/// ExpectedName/ExpectedServerUrl identity echo (ack is the existing ConsentAck, with the
/// coded <c>identity_mismatch</c> error on mismatch and nothing mutated) — again discovery
/// only, enforcement lives in the v2 frame itself; and <c>"status/1"</c> routes
/// StatusSubscribe to <see cref="DaemonStatusIpc"/>.
/// </summary>
internal static class LocalControlCapabilities {
    public static readonly IReadOnlyList<string> Current = ["consent/1", "consent/2", "consent/3", "status/1"];
}
