namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The daemon's advertised local-control capability list, surfaced on <c>HelloReply</c>.
/// INVARIANT: an entry may exist here ONLY if <see cref="LocalControlServer"/> actually
/// routes the corresponding frame(s) to a real handler — this list is assembled right next
/// to that routing switch so a capability can never be advertised without behavior behind
/// it. A later change appends <c>"status/1"</c> once <see cref="LocalControlServer"/> gains
/// a <c>StatusSubscribe</c> handler; until then it must not appear here.
/// </summary>
internal static class LocalControlCapabilities {
    public static readonly List<string> Current = ["consent/1"];
}
