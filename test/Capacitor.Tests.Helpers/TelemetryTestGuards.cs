using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Shared precondition check for the telemetry capture helpers.
///
/// <para>An empty <c>TestSink</c> surfaces as an opaque <c>Sequence contains no elements</c> from
/// whatever <c>Single()</c> runs next, which says nothing about why. This asserts the precondition
/// at the point it is established and, when it fails, reports WHICH of the independent reasons
/// applies — because a CI-only failure here was misdiagnosed once already.</para>
/// </summary>
public static class TelemetryTestGuards {
    /// <summary>Throws with a diagnosis if <see cref="CliTelemetry.Initialize"/> left telemetry off.</summary>
    public static void AssertEnabled(string command, ConfigRoot config) {
        if (CliTelemetry.Enabled) return;

        var decision = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled(config));

        throw new InvalidOperationException(
            $"CliTelemetry did not enable after Initialize(\"{command}\").\n"
          + $"  resolver   = {decision.Enabled} (reason={decision.Reason})\n"
          + $"  reportable = {CommandEvents.IsReportable(command)}\n"
          + $"  configRoot = {config.Directory}\n"
          + $"  DO_NOT_TRACK = {Env("DO_NOT_TRACK")}, KCAP_TELEMETRY = {Env("KCAP_TELEMETRY")}\n"
          + "Read it as: resolver=False -> an env var or the persisted flag disabled it; "
          + "reportable=False -> the command is denylisted; both True -> Initialize threw "
          + "internally and its catch disabled telemetry. (A failed device-id write can no longer "
          + "be the cause: TelemetryDeviceId.GetOrCreate() falling back to an in-memory id no "
          + "longer disables the facade.)");
    }

    static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { } v ? $"'{v}'" : "(unset)";
}
