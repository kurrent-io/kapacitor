using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The per-launch generated names for ONE hosted-agent launch: values that must be identical everywhere
/// they are consumed within that launch, and unguessable to the workspace the agent runs in.
///
/// <para><b>Why one type rather than values generated where needed.</b> Two independent derivations of the
/// same name is the defect this exists to make unrepresentable. Each name has two consumers — the result
/// channel's wire name is read by the injected MCP server spec AND by the vendor's allowlist argv; the
/// unmatchable name is read by that argv AND by the launch assertion — so a design where each consumer
/// generates its own can produce a launch whose allowlist does not admit its own result channel. That
/// failure is silent: the agent starts normally and can never report.</para>
///
/// <para><b>Why unguessable.</b> A vendor whose MCP gate is a name allowlist matches names exactly, and the
/// repository being worked in can declare MCP servers of its own. A predictable allowlisted name is
/// therefore a repository-impersonation hole — a repository naming its server that gets a process spawned as
/// the daemon user. Measured for Gemini; see the Gemini reviewer design spec §2.3 and §2.6/§2.7.</para>
///
/// <para><b>Vendor-neutral by shape, not by accident.</b> The two concepts — "the result channel's name on
/// the wire" and "a name nothing can match" — belong to any vendor with a name-matched MCP gate. This also
/// absorbs the per-launch deny-all name that was previously generated inline inside the argv substitution:
/// that generator had exactly the two-derivations shape described above, since the value never left the
/// substitution and nothing else could assert on it.</para>
/// </summary>
internal sealed record LaunchIdentity {
    /// <summary>What every reserved-name comparison sees — <c>KcapMcpRegistry</c>'s reservation check and
    /// Copilot's tool-id builder. For an aliasing vendor this never reaches the wire.</summary>
    public string ResultChannelCanonicalId { get; }

    /// <summary>The result channel's name as the vendor sees it. Equal to
    /// <see cref="ResultChannelCanonicalId"/> for every vendor that does not need aliasing, so their
    /// behaviour is byte-identical to before this type existed.</summary>
    public string ResultChannelWireName { get; }

    /// <summary>A name nothing can match, used as the deny-all value of a vendor's MCP allowlist so an
    /// interactive launch admits no repository-authored server.</summary>
    public string UnmatchableMcpName { get; }

    readonly bool _aliases;
    readonly Guid _allowlistSuffix;

    // Private: a caller can ask for a fresh identity but cannot construct an arbitrary one. This is the
    // seam where a fixed or reused name would otherwise slip in, and every argv-equality test in the suite
    // would still pass while the containment was gone.
    private LaunchIdentity(string canonicalId, string wireName, string unmatchableName, bool aliases, Guid allowlistSuffix) {
        ResultChannelCanonicalId = canonicalId;
        ResultChannelWireName    = wireName;
        UnmatchableMcpName       = unmatchableName;
        _aliases                 = aliases;
        _allowlistSuffix         = allowlistSuffix;
    }

    /// <summary>
    /// The name an injected NON-result-channel review server (an allowlist server, or the borrowed-snapshot
    /// review-context server) carries on the wire. For an aliasing vendor this is the canonical id plus the
    /// launch's allowlist suffix — the vendor's MCP gate is an exact-name allowlist that must admit these
    /// servers, and admitting the CANONICAL id would let the repository being worked in declare a server of
    /// that fixed, public name and have it spawned as the daemon user (the same impersonation shape measured
    /// for the result channel; see the Gemini reviewer design spec §2.3/§2.6). For every other vendor this
    /// returns the input unchanged, keeping their wire behaviour byte-identical.
    /// </summary>
    public string AllowlistWireName(string canonicalId) =>
        _aliases ? $"{canonicalId}-{_allowlistSuffix:N}" : canonicalId;

    /// <summary>
    /// The ONLY production entry point. Three INDEPENDENT v4 GUIDs, and no launch context in scope — so the
    /// names cannot be derived from a session id, an agent id, a worktree path or the clock even by
    /// accident, because none of those values is reachable from here.
    /// </summary>
    /// <param name="aliasResultChannel">True only for a vendor whose MCP gate is a name allowlist that must
    /// also admit our own channel, so the channel's name has to be unguessable too (Gemini today). False
    /// keeps the canonical id on the wire, which is what every other vendor has always sent.</param>
    public static LaunchIdentity ForLaunch(bool aliasResultChannel) =>
        FromGuids(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), aliasResultChannel);

    /// <summary>
    /// Test seam. Takes concrete <see cref="Guid"/> VALUES rather than a factory delegate: a delegate would
    /// close over its caller's scope, which is how an earlier revision of this design left
    /// derivation-from-launch-context legal while its tests still passed. A value cannot close over anything.
    /// </summary>
    internal static LaunchIdentity FromGuids(Guid resultChannel, Guid unmatchable, Guid allowlistSuffix, bool aliasResultChannel) {
        // No fallback, predictable or otherwise. The security property is that these strings are unguessable
        // at the instant the repository's own MCP declarations are read, so a degraded name is worse than no
        // agent at all. Independence is asserted rather than assumed: the wire name is visible to the agent
        // (it is in its own process argv), so a shared GUID would make the unmatchable name derivable from it.
        if (resultChannel == Guid.Empty || unmatchable == Guid.Empty || allowlistSuffix == Guid.Empty
         || resultChannel == unmatchable || resultChannel == allowlistSuffix || unmatchable == allowlistSuffix)
            throw new InvalidOperationException(
                "Refusing to build a launch identity: a generated name would be predictable or reused. A "
              + "vendor's MCP allowlist is an exact-name gate, so any of these names being guessable lets "
              + "the repository being worked in impersonate an injected review server.");

        return new(
            KcapMcpRegistry.ReservedResultChannelId,
            aliasResultChannel
                ? $"{KcapMcpRegistry.ReservedResultChannelId}-{resultChannel:N}"
                : KcapMcpRegistry.ReservedResultChannelId,
            $"kcap-deny-{unmatchable:N}",
            aliasResultChannel,
            allowlistSuffix);
    }
}
