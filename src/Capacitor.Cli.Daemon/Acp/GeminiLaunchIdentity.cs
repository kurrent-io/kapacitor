using Capacitor.Cli.Core;
namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Every per-launch generated name for ONE Gemini launch, created once before any argv is composed and
/// threaded to every consumer.
///
/// <para><b>Why a type rather than two locals.</b> Two independent derivations of the same name is the
/// defect this exists to make unrepresentable. Both names are consumed twice — the wire name by the injected
/// MCP server spec and by the allowlist argv, the deny-all name by the allowlist argv and by the launch
/// assertion — and a design where each consumer generates its own would produce a launch whose allowlist
/// does not admit its own result channel. That failure mode is silent: the reviewer starts happily and can
/// never report.</para>
///
/// <para><b>Why the names must be unguessable.</b> The vendor's MCP allowlist is an exact-name gate
/// (<c>Array.prototype.includes</c> on the raw name), and the repository under review can declare MCP
/// servers of its own. A predictable allowlisted name is therefore a repository-impersonation hole: a
/// repository naming its server that gets a process spawned as the daemon user. Measured — see the design
/// spec's §2.3 and §2.6/§2.7.</para>
///
/// <para><b>Why the two names must be independent.</b> The wire name is visible to the reviewer (it is in
/// its own process argv). If the deny-all name were derived from it, learning one would yield the other.</para>
/// </summary>
internal sealed record GeminiLaunchIdentity {
    /// <summary>What every reserved-name comparison sees — <c>KcapMcpRegistry</c>'s reservation check and
    /// Copilot's tool-id builder. Never reaches a vendor for Gemini.</summary>
    public string CanonicalId { get; }

    /// <summary>The review channel's name on the wire: the ONLY name handed to Gemini for a review launch,
    /// and the single value its MCP allowlist carries.</summary>
    public string WireName { get; }

    /// <summary>The interactive launch's allowlist value — a name nothing can match, which is how an
    /// interactive hosted Gemini is denied every repository-authored MCP server.</summary>
    public string DenyAllName { get; }

    // Private: production can ask for a fresh identity but cannot construct an arbitrary one. An earlier
    // revision of the design exposed the record shape and left construction unspecified, which is exactly
    // where a fixed or reused deny-all name would have slipped in.
    private GeminiLaunchIdentity(string canonicalId, string wireName, string denyAllName) {
        CanonicalId = canonicalId;
        WireName    = wireName;
        DenyAllName = denyAllName;
    }

    /// <summary>
    /// The ONLY production entry point. Two INDEPENDENT v4 GUIDs, and no launch context in scope — so the
    /// names cannot be derived from the session id, the agent id, the worktree path or the clock even by
    /// accident, because none of those values is reachable from here.
    /// </summary>
    public static GeminiLaunchIdentity ForLaunch() => FromGuids(Guid.NewGuid(), Guid.NewGuid());

    /// <summary>
    /// Test seam. Takes concrete <see cref="Guid"/> VALUES rather than a factory delegate: a delegate would
    /// close over its caller's scope, which is how an earlier revision left derivation-from-launch-context
    /// legal while the tests still passed. A value cannot close over anything.
    /// </summary>
    internal static GeminiLaunchIdentity FromGuids(Guid channel, Guid denyAll) {
        // No fallback, predictable or otherwise. The security property is that these strings are unguessable
        // at the instant the repository's MCP declarations are read, so a degraded name is worse than no
        // reviewer at all — and `channel == denyAll` is refused because independence is a property worth
        // asserting rather than assuming.
        if (channel == Guid.Empty || denyAll == Guid.Empty || channel == denyAll)
            throw new InvalidOperationException(
                "Refusing to build a Gemini launch identity: a generated name would be predictable or "
              + "reused. The vendor's MCP allowlist is an exact-name gate, so either name being guessable "
              + "lets the repository under review impersonate the reviewer's result channel.");

        return new(
            KcapMcpRegistry.ReservedResultChannelId,
            $"{KcapMcpRegistry.ReservedResultChannelId}-{channel:N}",
            $"kcap-deny-{denyAll:N}");
    }
}
