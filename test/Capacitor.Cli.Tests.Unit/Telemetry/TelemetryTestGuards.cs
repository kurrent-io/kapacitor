using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

/// <summary>
/// Shared precondition check for the telemetry capture helpers.
///
/// <para>An empty <c>TestSink</c> surfaces as an opaque <c>Sequence contains no elements</c> from
/// whatever <c>Single()</c> runs next, which says nothing about why. This asserts the precondition
/// at the point it is established and, when it fails, reports WHICH of the three independent
/// reasons applies — because a CI-only failure here was misdiagnosed once already.</para>
/// </summary>
static class TelemetryTestGuards {
    /// <summary>Throws with a diagnosis if <see cref="CliTelemetry.Initialize"/> left telemetry off.</summary>
    public static void AssertEnabled(string command) {
        if (CliTelemetry.Enabled) return;

        var decision = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled());
        var deviceId = TelemetryState.Read().Id;

        throw new InvalidOperationException(
            $"CliTelemetry did not enable after Initialize(\"{command}\").\n"
          + $"  resolver   = {decision.Enabled} (reason={decision.Reason})\n"
          + $"  reportable = {CommandEvents.IsReportable(command)}\n"
          + $"  deviceId   = {(deviceId is null ? "null" : "present")}\n"
          + $"  pathOverride = {TelemetryState.PathOverride ?? "(none)"}\n"
          + $"  DO_NOT_TRACK = {Env("DO_NOT_TRACK")}, KCAP_TELEMETRY = {Env("KCAP_TELEMETRY")}\n"
          + "Read it as: resolver=False -> an env var or the persisted flag disabled it; "
          + "reportable=False -> the command is denylisted; both True with deviceId=null -> the "
          + "device-id write failed and Initialize bailed; all three fine -> Initialize threw "
          + "internally and its catch disabled telemetry.");
    }

    static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { } v ? $"'{v}'" : "(unset)";
}
