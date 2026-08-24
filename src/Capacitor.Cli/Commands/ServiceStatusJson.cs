using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>Machine-readable payload for <c>kcap daemon service status --json</c> (spec §3.4).</summary>
/// <remarks>
/// The four <c>Unit*</c> members are UX evidence only, re-derived by re-reading the installed unit's
/// baked environment (see <see cref="ServiceStatusRender.Render"/>) — they are not sourced from
/// <see cref="ServiceQuery"/> and are null whenever that re-read is ambiguous or fails. Additive-last so
/// existing consumers of the earlier positional shape are unaffected.
/// </remarks>
public sealed record ServiceStatusJson(
    string ServiceId, bool UnitPresent, string State, string? BinaryPath,
    string? InstallBinaryPath, int? JobPid, int? DaemonPid, bool TxnMarker, bool TxnActive,
    string? UnitProfile = null, string? UnitServerUrl = null,
    string? UnitExpectedServer = null, string? UnitConsentSeed = null);

/// <summary>Machine-readable outcome for <c>kcap daemon service ensure</c> — what the ladder did, and
/// (on a refusal) which recovery surface the flow should offer. <see cref="Recovery"/> is non-null only
/// on a gate refusal (takeover/reinstall/attention); <see cref="Reason"/> is the <c>start_gate_reason=</c>
/// token for a gate refusal, the <c>verify_*</c> token for any other verified-transaction failure, or
/// <c>plain_failure</c> for a degraded (non-launchd) failure. <see cref="Verified"/> reports whether THIS
/// run performed the launchd verified transaction — false on plain install/start and on no-op rows;
/// the flow's copy must key off <see cref="Outcome"/>, not off <c>verified</c>.</summary>
public sealed record ServiceEnsureJson(
    string ServiceId, string State, string Action, string Outcome,
    string? Recovery = null, string? Reason = null, bool Verified = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ServiceStatusJson))]
[JsonSerializable(typeof(ServiceEnsureJson))]
public partial class ServiceJsonContext : JsonSerializerContext;

/// <summary>Pure renderer for the status JSON — kept separate from I/O so it's directly testable.</summary>
internal static class ServiceStatusRender {
    /// <summary>
    /// Returns (json, exitCode). <paramref name="q"/>.Probe == <see cref="LabelProbe.Unknown"/> means the
    /// launchd probe itself failed to classify the service, so no JSON is emitted — an unknown state must
    /// never masquerade as the well-defined "not_installed" one.
    /// </summary>
    public static (string? Json, int ExitCode) Render(
            ServiceQuery q, string serviceId, string? installBinaryPath, int? daemonPid, bool txnMarker, bool txnActive,
            string? unitProfile = null, string? unitServerUrl = null,
            string? unitExpectedServer = null, string? unitConsentSeed = null) {
        if (q.Probe == LabelProbe.Unknown) return (null, 1);

        var state = q.State switch {
            ServiceState.NotInstalled => "not_installed",
            ServiceState.Installed    => "installed",
            ServiceState.Running      => "running",
            _                         => "not_installed",
        };

        var dto = new ServiceStatusJson(
            serviceId, q.UnitPresent, state, q.BinaryPath,
            installBinaryPath, q.JobPid, daemonPid, txnMarker, txnActive,
            unitProfile, unitServerUrl, unitExpectedServer, unitConsentSeed);

        return (JsonSerializer.Serialize(dto, ServiceJsonContext.Default.ServiceStatusJson), 0);
    }
}
