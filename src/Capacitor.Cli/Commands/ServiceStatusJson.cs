using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>Machine-readable payload for <c>kcap daemon service status --json</c> (spec §3.4).</summary>
public sealed record ServiceStatusJson(
    string ServiceId, bool UnitPresent, string State, string? BinaryPath,
    string? InstallBinaryPath, int? JobPid, int? DaemonPid, bool TxnMarker, bool TxnActive);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ServiceStatusJson))]
public partial class ServiceJsonContext : JsonSerializerContext;

/// <summary>Pure renderer for the status JSON — kept separate from I/O so it's directly testable.</summary>
internal static class ServiceStatusRender {
    /// <summary>
    /// Returns (json, exitCode). <paramref name="q"/>.Probe == <see cref="LabelProbe.Unknown"/> means the
    /// launchd probe itself failed to classify the service, so no JSON is emitted — an unknown state must
    /// never masquerade as the well-defined "not_installed" one.
    /// </summary>
    public static (string? Json, int ExitCode) Render(
        ServiceQuery q, string serviceId, string? installBinaryPath, int? daemonPid, bool txnMarker, bool txnActive) {
        if (q.Probe == LabelProbe.Unknown) return (null, 1);

        var state = q.State switch {
            ServiceState.NotInstalled => "not_installed",
            ServiceState.Installed    => "installed",
            ServiceState.Running      => "running",
            _                         => "not_installed",
        };

        var dto = new ServiceStatusJson(
            serviceId, q.UnitPresent, state, q.BinaryPath,
            installBinaryPath, q.JobPid, daemonPid, txnMarker, txnActive);

        return (JsonSerializer.Serialize(dto, ServiceJsonContext.Default.ServiceStatusJson), 0);
    }
}
