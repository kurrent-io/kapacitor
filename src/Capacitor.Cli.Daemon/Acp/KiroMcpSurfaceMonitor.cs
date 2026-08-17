using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Watches Kiro's own MCP notifications and trips when the reviewer's callable surface is not
/// exactly what this launch injected.
///
/// <para><b>This is a tripwire, not the containment.</b> Containment is source suppression — the
/// empty per-launch <see cref="KiroReviewerHome"/> and the worktree layer's removal of
/// branch-authored config. What this adds is DETECTION of a suppression failure, which is why its
/// residual (below) degrades detection rather than the boundary.</para>
///
/// <para><b>Why not a certified-version set</b> (the Gemini shape): there, the vendor's name-matching
/// semantics ARE the boundary, so a build change can void consent. Here the ordinary regression —
/// Kiro stops honouring <c>KIRO_HOME</c>, or gains a second global config source — surfaces as global
/// servers initializing under names outside the injected set, which is exactly what this catches.
/// <see cref="Capacitor.Cli.Core.ReviewerVersionStore"/> handles the build-change axis separately.</para>
///
/// <para><b>Enforced continuously, not sampled.</b> These are asynchronous notifications and the
/// protocol has no initialization-complete event, so there is no barrier to wait for. Sampling after
/// a settle has a gap by construction: a server can initialize, be used, and be missed between two
/// samples. Judging each notification on arrival closes it.</para>
///
/// <para><b>Membership, not counting.</b> This originally required each injected name to initialize
/// exactly once, reasoning that a duplicate could be a second server standing up under an injected
/// name. But every injected name carries a per-launch GUID (Kiro aliases the result channel and the
/// allowlist servers), so nothing in the operator's global config can be standing under one — a
/// suppression failure surfaces as the operator's own server NAMES, which the membership arm catches.
/// And kiro-cli 2.16.0 announces one injected server's initialization more than once as ordinary
/// behaviour, so the count rule reaped every reviewer launch while the surface was exactly the
/// injected set. A repeated announce of an injected name is therefore benign.</para>
///
/// <para><b>Known residual, not mitigated:</b> a build that stopped emitting
/// <c>server_initialized</c> for extra servers while still emitting it for injected ones defeats
/// this, and nothing here detects that. Accepted because the tripwire is not the boundary, both
/// suppression mechanisms would have to fail in the same build as the notification change, and the
/// exposure it would hide is bounded by the read surface the operator already consented to.</para>
/// </summary>
internal sealed class KiroMcpSurfaceMonitor {
    internal const string InitializedMethod = "_kiro.dev/mcp/server_initialized";
    internal const string InitFailureMethod = "_kiro.dev/mcp/server_init_failure";

    readonly IReadOnlySet<string> _injected;
    readonly string _resultChannelWireName;
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly object _gate = new();

    string? _violation;

    internal KiroMcpSurfaceMonitor(IReadOnlySet<string> injectedWireNames, string resultChannelWireName) {
        _injected = injectedWireNames;
        _resultChannelWireName = resultChannelWireName;
    }

    /// <summary>The coded failure, or null while the surface is still exactly the injected set.
    /// Sticky: the first violation is the one reported, because a later one may be a consequence.</summary>
    internal string? Violation { get { lock (_gate) return _violation; } }

    /// <summary>
    /// Whether the result channel has actually started. A readiness check, NOT the containment check
    /// — requiring it catches only TOTAL silence, never the selective omission described above.
    /// </summary>
    internal bool ResultChannelReady {
        get { lock (_gate) return _seen.Contains(_resultChannelWireName); }
    }

    /// <summary>Judged on arrival, on the connection's own read loop.</summary>
    internal void Observe(AcpNotification notification) {
        if (notification.Method is not (InitializedMethod or InitFailureMethod)) return;
        if (notification.Params is not { } payload) return;
        if (!payload.TryGetProperty("serverName", out var nameElement)) return;
        if (nameElement.GetString() is not { Length: > 0 } serverName) return;

        lock (_gate) {
            if (_violation is not null) return;   // sticky

            if (notification.Method == InitFailureMethod) {
                // Only the result channel's failure is fatal: without it the reviewer cannot report at
                // all, and the round would otherwise die as a silent timeout with no diagnosis.
                if (string.Equals(serverName, _resultChannelWireName, StringComparison.Ordinal))
                    _violation =
                        "kiro_reviewer_result_channel_unavailable: the injected result channel failed to "
                      + "start, so the reviewer has no way to deliver a result. Failing the round now "
                      + "rather than letting it time out with no diagnosis.";
                return;
            }

            if (!_injected.Contains(serverName)) {
                _violation =
                    $"kiro_reviewer_mcp_surface_unexpected: MCP server '{serverName}' initialized but is "
                  + "not one this launch injected. The reviewer's callable surface must be exactly the "
                  + "injected set, so the isolated KIRO_HOME is no longer suppressing the operator's "
                  + "global servers.";
                return;
            }

            // Recorded for readiness, never counted: kiro-cli (2.16.0) announces one injected server's
            // initialization more than once, and an injected name is a per-launch GUID nothing else can
            // stand up under — see the class doc.
            _seen.Add(serverName);
        }
    }

    /// <summary>The wire names a launch's injected specs carry — the monitor's expected set.</summary>
    internal static IReadOnlySet<string> InjectedNames(IReadOnlyList<AcpMcpServerSpec> injected) =>
        injected.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Builds a monitor for a review launch, or null when this vendor/launch is not one.</summary>
    internal static KiroMcpSurfaceMonitor? For(
            AcpVendorDescriptor descriptor, bool isReviewFlow,
            IReadOnlyList<AcpMcpServerSpec>? injected, LaunchIdentity? identity) =>
        isReviewFlow
     && descriptor.Vendor == AcpVendorDescriptors.Kiro.Vendor
     && injected is { Count: > 0 }
     && identity is not null
            ? new KiroMcpSurfaceMonitor(InjectedNames(injected), identity.ResultChannelWireName)
            : null;
}
