using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// App-side attach state, projected from Capacitor.Cli.Core.LocalIpc.LocalControlEvent.
public enum AttachState { Connecting, Connected, Unreachable }

/// One atomic value combining state, reason, and capabilities (design decision 8): split
/// state/reason observables that can tear are forbidden. Capabilities are null on every
/// non-connected state — never retained from a previous incarnation. DaemonVersion carries the
/// hello reply's version on Unreachable (spec decision 6); it is null on Connecting/Connected —
/// Connected's version arrives via snapshots, not attach status. Identity carries Connected's
/// hello-derived ConnectedIdentity; null on every non-Connected state.
public sealed record AttachStatus(
    AttachState State, string? Reason, IReadOnlyList<string>? Capabilities, string? DaemonVersion = null,
    ConnectedIdentity? Identity = null);

/// Outcome of a <c>kcap daemon start -d --name &lt;name&gt;</c> spawn attempt.
public sealed record StartDaemonResult(bool Ok, string? Message);
