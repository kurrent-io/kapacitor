using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Services.Mutation;

/// <summary>
/// The app's attribution policy over <see cref="BootRefusalMarker"/>. Reading and writing the marker
/// belongs to that type; what belongs here is which markers this lane is entitled to claim.
///
/// <para>For DETACHED starts the mutation lane is the marker's single consumer — attribution requires
/// the lane's own attempt GUID, never a foreign one. That makes this the DUAL of
/// <c>ServiceVerify.Attributable</c>, which claims service-unit refusals and so requires the ABSENCE
/// of an attempt id: a marker satisfies at most one of the two.</para>
/// </summary>
public static class BootRefusalAttribution {
    /// <summary>
    /// Claims the marker only against a verifiable identity (schema, attemptId, daemon, token,
    /// instance id, pid, expectation). Any mismatch returns null and leaves the marker untouched.
    /// </summary>
    public static BootRefusalRecord? TryAttribute(DaemonStore store, string daemonName, string attemptId, string? requestCanonicalServer) {
        if (BootRefusalMarker.TryRead(store, daemonName) is not { } evidence) return null;

        if (evidence.Schema != BootRefusalMarker.CurrentSchema) return null;
        if (evidence.AttemptId is null || evidence.AttemptId != attemptId) return null;
        // Raw, unlike the dual: the attempt id already proves this marker came from our own spawn,
        // which passed this exact spelling as --name.
        if (evidence.DaemonName != daemonName) return null;
        if (string.IsNullOrEmpty(evidence.Token)) return null;
        if (evidence.Pid <= 0) return null;
        if (string.IsNullOrEmpty(evidence.InstanceId)) return null;
        if (!ServerIdentity.Matches(evidence.Expectation, requestCanonicalServer)) return null;

        BootRefusalMarker.TryDelete(store, daemonName);   // consumed: already attributed
        return evidence;
    }
}
