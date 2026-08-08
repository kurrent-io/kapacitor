using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

// Shares the TelemetryState.PathOverride lock key with TelemetryStateTests and CliTelemetryTests
// (Task 2's convention): keying on the resource, not the class, so any test class touching this
// shared static serialises against every other one.
[NotInParallel(nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride))]
public class SetupFunnelTests {
    static List<TelemetryEvent> StartCapturing() {
        TelemetryState.PathOverride =
            Path.Combine(Path.GetTempPath(), $"kcap-funnel-{Guid.NewGuid():N}", "telemetry.json");
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("setup", null, loggedIn: false);
        sink.Clear();   // drop cli_first_run

        return sink;
    }

    [Test]
    public async Task Happy_path_emits_the_full_sequence() {
        var sink = StartCapturing();

        SetupFunnel.Started(hasExistingProfile: false, serverUrlProvided: false, noPrompt: false);
        SetupFunnel.SigninOpened("browser", "workos");
        SetupFunnel.SigninCompleted("workos");
        SetupFunnel.TenantNone("workos");
        SetupFunnel.WorkspaceOffered();
        SetupFunnel.WorkspaceRequested();
        SetupFunnel.WorkspaceProvisioned();
        SetupFunnel.Succeeded(agentsConfigured: 3);

        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(new[] {
            "cli_setup_started", "cli_setup_signin_opened", "cli_setup_signin_completed",
            "cli_setup_tenant_none", "cli_setup_workspace_offered", "cli_setup_workspace_requested",
            "cli_setup_workspace_provisioned", "cli_setup_succeeded",
        });
    }

    [Test]
    public async Task Abandoned_at_signup_stops_after_the_offer() {
        var sink = StartCapturing();

        SetupFunnel.Started(false, false, false);
        SetupFunnel.SigninCompleted("workos");
        SetupFunnel.TenantNone("workos");
        SetupFunnel.WorkspaceOffered();
        SetupFunnel.WorkspaceDeclined();

        await Assert.That(sink[^1].Name).IsEqualTo("cli_setup_workspace_declined");
        await Assert.That(sink.Any(e => e.Name == "cli_setup_succeeded")).IsFalse();
    }

    [Test]
    public async Task Provisioning_failure_carries_a_reason() {
        var sink = StartCapturing();

        SetupFunnel.WorkspaceFailed("slug_taken");

        await Assert.That(sink[^1].Name).IsEqualTo("cli_setup_workspace_failed");
        await Assert.That(sink[^1].Properties["reason"]!.GetValue<string>()).IsEqualTo("slug_taken");
    }

    [Test]
    public async Task Started_carries_its_entry_conditions() {
        var sink = StartCapturing();

        SetupFunnel.Started(hasExistingProfile: true, serverUrlProvided: true, noPrompt: true);

        var props = sink[0].Properties;
        await Assert.That(props["has_existing_profile"]!.GetValue<bool>()).IsTrue();
        await Assert.That(props["server_url_provided"]!.GetValue<bool>()).IsTrue();
        await Assert.That(props["no_prompt"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Succeeded_reports_a_count_not_vendor_names() {
        var sink = StartCapturing();

        SetupFunnel.Succeeded(agentsConfigured: 4);

        await Assert.That(sink[^1].Properties["agents_configured"]!.GetValue<int>()).IsEqualTo(4);
    }

    // Guards the collision with the server's own cli_setup_completed.
    [Test]
    public async Task No_funnel_event_collides_with_a_server_event_name() {
        string[] serverEvents = [
            "user_registered", "user_logged_in", "cli_setup_completed", "session_ingest_started",
            "session_ingest_ended", "eval_ran", "fact_retained", "daemon_connected",
            "daemon_disconnected", "hosted_agent_started", "hosted_agent_ended",
        ];

        var sink = StartCapturing();
        SetupFunnel.Started(false, false, false);
        SetupFunnel.SigninOpened("browser", "workos");
        SetupFunnel.SigninCompleted("workos");
        SetupFunnel.SigninFailed("timeout");
        SetupFunnel.TenantNone("workos");
        SetupFunnel.WorkspaceOffered();
        SetupFunnel.WorkspaceDeclined();
        SetupFunnel.WorkspaceRequested();
        SetupFunnel.WorkspaceProvisioned();
        SetupFunnel.WorkspaceFailed("poll_timeout");
        SetupFunnel.Succeeded(1);

        foreach (var e in sink)
            await Assert.That(serverEvents.Contains(e.Name)).IsFalse();
    }
}
