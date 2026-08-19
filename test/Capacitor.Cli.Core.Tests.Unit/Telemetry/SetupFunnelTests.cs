using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using NSubstitute;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

// Shares the TelemetryState.PathOverride and TelemetryDeviceId.PathOverride lock keys with
// TelemetryStateTests/CliTelemetryTests/TelemetryDeviceIdTests (Task 2's convention, extended for
// the device-id split): keying on the resource, not the class, so any test class touching either
// shared static serialises against every other one.
[NotInParallel([
    nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride),
    nameof(TelemetryDeviceId) + "." + nameof(TelemetryDeviceId.PathOverride),
])]
public class SetupFunnelTests {
    [TempDir] public required TempDir Tmp { get; init; }

    // CliTelemetry holds process-global static state (Enabled, TestSink, ...). A prior test
    // elsewhere in the suite (e.g. one that persists `telemetry off`) can leave Enabled=false
    // behind via CliTelemetry.DiscardAndDisable — reset before touching TestSink so every test
    // here starts from pristine state rather than inheriting whatever ran before it.
    [Before(Test)]
    public void ResetTelemetry() => CliTelemetry.Reset();

    List<TelemetryEvent> StartCapturing() {
        TelemetryState.PathOverride    = Tmp.PathTo("telemetry.json");
        TelemetryDeviceId.PathOverride = Tmp.PathTo("telemetry-device.json");
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("setup", null, loggedIn: false);

        TelemetryTestGuards.AssertEnabled("setup");

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

        // CollectionOrdering.Matching: IsEquivalentTo defaults to set comparison (any order) and
        // would pass even on a transposed sequence — this is exactly the funnel's ordering that
        // matters (a PostHog ordered funnel converts on step order, not just step presence).
        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(new[] {
            "cli_setup_started", "cli_setup_signin_opened", "cli_setup_signin_completed",
            "cli_setup_tenant_none", "cli_setup_workspace_offered", "cli_setup_workspace_requested",
            "cli_setup_workspace_provisioned", "cli_setup_succeeded",
        }, CollectionOrdering.Matching);
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

        // Mixed, not all-true: all-true would pass under a transposed mapping (e.g.
        // ["no_prompt"] = serverUrlProvided) just as easily as the correct one.
        SetupFunnel.Started(hasExistingProfile: true, serverUrlProvided: false, noPrompt: true);

        var props = sink[0].Properties;
        await Assert.That(props["has_existing_profile"]!.GetValue<bool>()).IsTrue();
        await Assert.That(props["server_url_provided"]!.GetValue<bool>()).IsFalse();
        await Assert.That(props["no_prompt"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Succeeded_reports_a_count_not_vendor_names() {
        var sink = StartCapturing();

        SetupFunnel.Succeeded(agentsConfigured: 4);

        await Assert.That(sink[^1].Properties["agents_configured"]!.GetValue<int>()).IsEqualTo(4);
    }

    // Guards collisions with events OTHER producers own: the server's own cli_setup_completed,
    // and cli_auth_return, which kcap-web's Worker emits and the CLI never does. Two producers
    // sharing a name would double-count across two different persons.
    [Test]
    public async Task No_funnel_event_collides_with_a_server_event_name() {
        string[] serverEvents = [
            "user_registered", "user_logged_in", "cli_setup_completed", "session_ingest_started",
            "session_ingest_ended", "eval_ran", "fact_retained", "daemon_connected",
            "daemon_disconnected", "hosted_agent_started", "hosted_agent_ended",
            "cli_auth_return",
        ];

        var sink = StartCapturing();
        SetupFunnel.Started(false, false, false);
        SetupFunnel.SigninOpened("browser", "workos");
        SetupFunnel.SigninCompleted("workos");
        SetupFunnel.SigninFailed("timeout");
        SetupFunnel.TenantNone("workos");
        SetupFunnel.WorkspaceOffered();
        SetupFunnel.WorkspaceDeclined();
        SetupFunnel.WorkspaceRedirected();
        SetupFunnel.WorkspaceRequested();
        SetupFunnel.WorkspaceProvisioned();
        SetupFunnel.WorkspaceFailed("poll_timeout");
        SetupFunnel.Succeeded(1);

        // Without this, an Emit that silently no-ops would leave sink empty and the loop below
        // would assert nothing — a test that cannot fail. 12 = the 12 calls above.
        await Assert.That(sink.Count).IsEqualTo(12);

        foreach (var e in sink)
            await Assert.That(serverEvents.Contains(e.Name)).IsFalse();
    }

    // Call-site coverage, not just SetupFunnel's statics: every test above calls SetupFunnel
    // directly, so a wiring defect inside WorkOSDiscovery.DiscoverAsync itself — e.g. the original
    // bug of anchoring signin_completed/signin_failed on the overall outcome instead of the
    // live-auth result — was invisible to this suite. A zero-tenant, no-provisioner run reaches
    // the legacy "ask your admin" dead-end (NoTenants) despite sign-in having fully succeeded,
    // which is exactly the case that anchoring on the overall outcome gets wrong.
    [Test]
    public async Task WorkOSDiscovery_emits_signin_completed_before_tenant_none_for_a_zero_tenant_run() {
        var sink = StartCapturing();

        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.None)));

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

        // No provisioner passed -> the legacy "ask your admin" dead-end -> NoTenants, even though
        // sign-in itself worked fine.
        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.NoTenants>();

        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(
            new[] { "cli_setup_signin_completed", "cli_setup_tenant_none" }, CollectionOrdering.Matching);
    }
}
