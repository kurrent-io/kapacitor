namespace Capacitor.App.Services;

/// App-side attach state, projected from Capacitor.Cli.Core.LocalIpc.LocalControlEvent.
public enum AttachState { Connecting, Connected, Unreachable }

/// One atomic value combining state, reason, and capabilities (design decision 8): split
/// state/reason observables that can tear are forbidden. Capabilities are null on every
/// non-connected state — never retained from a previous incarnation.
public sealed record AttachStatus(AttachState State, string? Reason, IReadOnlyList<string>? Capabilities);

/// Outcome of a `kcap daemon start -d --name <name>` spawn attempt.
public sealed record StartDaemonResult(bool Ok, string? Message);
