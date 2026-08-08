using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

// Shares the TelemetryState.PathOverride lock key with TelemetryStateTests (Task 2's convention):
// keying on the resource, not the class, so any test class touching this shared static serialises
// against every other one. This class also mutates CliTelemetry's own statics (TestSink, the
// Initialize-set state), which the same lock covers.
[NotInParallel(nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride))]
public class CliTelemetryTests {
    static string NewStatePath() =>
        Path.Combine(Path.GetTempPath(), $"kcap-facade-{Guid.NewGuid():N}", "telemetry.json");

    static List<TelemetryEvent> StartCapturing(string command = "setup", string? serverUrl = null) {
        TelemetryState.PathOverride = NewStatePath();
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize(command, serverUrl, loggedIn: false);

        return sink;
    }

    [Test]
    public async Task Capture_records_the_event_with_shared_properties() {
        // StartCapturing() always begins from a brand-new device state file (fresh NewStatePath()),
        // so Initialize's first-run notice fires and "cli_first_run" lands in the sink too (see
        // First_run_emits_cli_first_run_once_per_device below) — filter by name rather than assume
        // this is the only event, the same way Record_command_emits_cli_command_with_exit_code does.
        var sink = StartCapturing();

        CliTelemetry.Capture("cli_setup_started", new JsonObject { ["no_prompt"] = false });

        var e = sink.Single(x => x.Name == "cli_setup_started");
        await Assert.That(e.Properties["source"]!.GetValue<string>()).IsEqualTo("cli");
        await Assert.That(e.Properties.ContainsKey("cli_version")).IsTrue();
        await Assert.That(e.Properties.ContainsKey("os")).IsTrue();
        await Assert.That(e.Properties.ContainsKey("arch")).IsTrue();
        await Assert.That(e.Properties.ContainsKey("is_ci")).IsTrue();
        await Assert.That(e.Properties["no_prompt"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Record_command_emits_cli_command_with_exit_code() {
        var sink = StartCapturing("daemon");

        CliTelemetry.RecordCommand("daemon", ["daemon", "start", "--foreground"], exitCode: 0, durationMs: 42);

        var e = sink.Single(x => x.Name == "cli_command");
        await Assert.That(e.Properties["command"]!.GetValue<string>()).IsEqualTo("daemon");
        await Assert.That(e.Properties["subcommand"]!.GetValue<string>()).IsEqualTo("start");
        await Assert.That(e.Properties["exit_code"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(e.Properties["duration_ms"]!.GetValue<long>()).IsEqualTo(42L);
    }

    [Test]
    public async Task Denylisted_commands_emit_nothing() {
        var sink = StartCapturing("hook");

        CliTelemetry.RecordCommand("hook", ["hook", "--claude"], exitCode: 0, durationMs: 5);

        await Assert.That(sink.Any(x => x.Name == "cli_command")).IsFalse();
    }

    [Test]
    public async Task Disabled_telemetry_captures_nothing() {
        TelemetryState.PathOverride = NewStatePath();
        TelemetryState.SetEnabled(false);
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("setup", null, loggedIn: false);

        CliTelemetry.Capture("cli_setup_started", new JsonObject());
        CliTelemetry.RecordCommand("setup", ["setup"], 0, 1);

        await Assert.That(CliTelemetry.Enabled).IsFalse();
        await Assert.That(sink.Count).IsEqualTo(0);
    }

    // An uninitialised facade must be inert, not merely non-throwing: a swallowed exception and
    // a correctly-skipped capture look identical from the outside unless state is asserted.
    [Test]
    public async Task Capture_before_initialize_is_inert() {
        CliTelemetry.Reset();
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;

        CliTelemetry.Capture("orphan", new JsonObject());
        CliTelemetry.RecordCommand("status", ["status"], 0, 1);
        await CliTelemetry.FlushAndClose();

        await Assert.That(CliTelemetry.Enabled).IsFalse();
        await Assert.That(sink.Count).IsEqualTo(0);
    }

    [Test]
    public async Task First_run_emits_cli_first_run_once_per_device() {
        var path = NewStatePath();

        TelemetryState.PathOverride = path;
        var firstSink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = firstSink;
        CliTelemetry.Initialize("setup", null, loggedIn: false);

        TelemetryState.PathOverride = path;
        var secondSink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = secondSink;
        CliTelemetry.Initialize("status", null, loggedIn: false);

        await Assert.That(firstSink.Any(e => e.Name == "cli_first_run")).IsTrue();
        await Assert.That(secondSink.Any(e => e.Name == "cli_first_run")).IsFalse();
    }
}
