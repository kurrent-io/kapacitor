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
/// (on a refusal) which recovery surface the flow should offer. <see cref="Recovery"/> is non-null on
/// a refusal the engine mapped to a surface: the start gate (exit 28, mapped takeover/reinstall/
/// attention via the pinned <see cref="Capacitor.Cli.Core.ReasonRouting"/> table), the gate's TOCTOU
/// re-check drift (exit 29, always attention — never auto-retried, matching the app's own table), a
/// gated-install viability abort with the engine's coded <c>package_inconsistent</c> reason
/// (reinstall), or an attributed readiness-timeout boot refusal (its marker token routed through
/// <see cref="Capacitor.Cli.Core.ReasonRouting.ForBootRefusal"/> — takeover/storage/attention).
/// <see cref="Reason"/> is the <c>start_gate_reason=</c> token for a start-gate refusal,
/// <c>verify_start_gate_drift</c> for drift, the coded viability/boot-refusal token when one was
/// attributed, the <c>verify_*</c> token for any other verified-transaction failure, or
/// <c>plain_failure</c> for a degraded (non-launchd) failure. <see cref="Verified"/> reports whether
/// THIS run performed the launchd verified transaction — true on launchd refusals as well as
/// successes, false on plain install/start and on no-op rows; the flow's copy must key off
/// <see cref="Outcome"/>, not off <c>verified</c>.</summary>
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
