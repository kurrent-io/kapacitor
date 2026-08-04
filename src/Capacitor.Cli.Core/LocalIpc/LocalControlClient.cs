namespace Capacitor.Cli.Core.LocalIpc;

/// Typed events from LocalControlClient.RunAsync. BCL-only — this file is compiled into the
/// NativeAOT CLI/daemon, so no Rx types may appear on this surface.
public abstract record LocalControlEvent {
    public sealed record Connecting : LocalControlEvent;

    /// Carries the FIRST validated snapshot: a consumer that gates rendering on Connected can
    /// never observe the connected state while holding only a previous incarnation's data.
    public sealed record Connected(
        IReadOnlyList<string>? Capabilities, DaemonStatusDto FirstSnapshot) : LocalControlEvent;

    /// Reason is "daemon_unreachable" (transport/unresponsive) or "daemon_incompatible"
    /// (protocol evidence — a heuristic that background retries self-correct).
    public sealed record Unreachable(string Reason) : LocalControlEvent;

    public sealed record Status(DaemonStatusDto Snapshot) : LocalControlEvent;
}

/// Structural validity for DaemonStatus payloads: STJ source-gen leaves declared-non-nullable
/// members null on absent/null JSON, so the client validates before yielding — an app may
/// dereference every field of a yielded snapshot. Id uniqueness is load-bearing for keyed
/// diffing downstream.
internal static class DaemonStatusValidator {
    internal static bool IsValid(DaemonStatusDto? dto) {
        if (dto?.Daemon is not { } d || dto.Agents is not { } agents) return false;
        if (d.Name is null || d.Version is null || d.ServerUrl is null || d.Connection is null) return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in agents) {
            if (a is null || string.IsNullOrWhiteSpace(a.Id)) return false;
            if (a.Kind is null || a.Vendor is null || a.Status is null) return false;
            if (!seen.Add(a.Id)) return false;
        }
        return true;
    }
}
