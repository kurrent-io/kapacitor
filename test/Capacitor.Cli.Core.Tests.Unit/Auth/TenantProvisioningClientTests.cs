using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

// Reads SetupJoin.Current (a telemetry static), so it shares the same resource lock as the other
// telemetry-static tests (SetupFunnelTests/CliTelemetryTests) and cannot run beside one that mints.
[NotInParallel([
    nameof(CliTelemetry) + "." + nameof(CliTelemetry.TestSink),
])]
public class TenantProvisioningClientTests {
    [Test]
    public async Task ProvisionAsync_sends_bearer_and_camelCase_body_and_parses_202() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/provision").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202)
                .WithBody("""{"slug":"acme","state":"provisioning"}""")
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var outcome = await client.ProvisionAsync(server.Urls[0], "tok", "Acme Inc", "acme", CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(202);
        await Assert.That(outcome.Body!.State).IsEqualTo("provisioning");

        var log = server.FindLogEntries(Request.Create().WithPath("/api/signup/provision").UsingPost());
        await Assert.That(log.Count).IsEqualTo(1);
        var req = log[0].RequestMessage;
        await Assert.That(req.Headers!["Authorization"][0]).IsEqualTo("Bearer tok");
        var body = JsonNode.Parse(req.Body!)!;
        await Assert.That(body["orgName"]!.GetValue<string>()).IsEqualTo("Acme Inc");
        await Assert.That(body["slug"]!.GetValue<string>()).IsEqualTo("acme");
        await Assert.That(body["tier"]!.GetValue<string>()).IsEqualTo("free");
    }

    [Test]
    public async Task ProvisionAsync_parses_409_reason() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/provision").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409)
                .WithBody("""{"reason":"taken"}""").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var outcome = await client.ProvisionAsync(server.Urls[0], "tok", "Acme", "acme", CancellationToken.None);
        await Assert.That(outcome.StatusCode).IsEqualTo(409);
        await Assert.That(outcome.Body!.Reason).IsEqualTo("taken");
    }

    [Test]
    public async Task CheckAvailabilityAsync_parses_reason() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/availability").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"available":false,"reason":"reserved"}""").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var avail = await client.CheckAvailabilityAsync(server.Urls[0], "tok", "acme", CancellationToken.None);
        await Assert.That(avail!.Available).IsFalse();
        await Assert.That(avail.Reason).IsEqualTo("reserved");
    }

    [Test]
    public async Task GetStatusAsync_parses_active_with_workosOrgId() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"state":"active","url":"https://acme.kcap.ai","workosOrgId":"org_live"}""")
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var status = await client.GetStatusAsync(server.Urls[0], "tok", "acme", CancellationToken.None);
        await Assert.That(status.StatusCode).IsEqualTo(200);
        await Assert.That(status.Body!.State).IsEqualTo("active");
        await Assert.That(status.Body.WorkosOrgId).IsEqualTo("org_live");
    }

    [Test]
    public async Task GetStatusAsync_surfaces_401_so_the_poll_is_not_blind() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithBody("""{"error":"unauthenticated"}""").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var status = await client.GetStatusAsync(server.Urls[0], "tok", "acme", CancellationToken.None);
        await Assert.That(status.StatusCode).IsEqualTo(401);
        await Assert.That(status.Body).IsNull();
    }

    [Test]
    public async Task GetStatusAsync_surfaces_404_ownership_mismatch() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404)
                .WithBody("""{"error":"not_found"}""").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var status = await client.GetStatusAsync(server.Urls[0], "tok", "acme", CancellationToken.None);
        await Assert.That(status.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task GetStatusAsync_returns_status_zero_on_transport_failure() {
        var server = WireMockServer.Start();
        var url = server.Urls[0];
        server.Stop();

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var status = await client.GetStatusAsync(url, "tok", "acme", CancellationToken.None);
        await Assert.That(status.StatusCode).IsEqualTo(0);
        await Assert.That(status.Body).IsNull();
    }

    [Test]
    public async Task CheckAvailabilityAsync_returns_null_on_non_json_body() {
        using var server = WireMockServer.Start();
        // 200 but an unreadable body (proxy/error page) must degrade to null, not throw.
        server.Given(Request.Create().WithPath("/api/signup/availability").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("<html>not json</html>").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var avail = await client.CheckAvailabilityAsync(server.Urls[0], "tok", "acme", CancellationToken.None);
        await Assert.That(avail).IsNull();
    }

    [Test]
    public async Task ProvisionAsync_returns_status_zero_on_transport_failure() {
        // Start then stop the server so the connection is refused — a transport failure
        // must degrade to StatusCode 0 (caller maps to a failed offer), never throw.
        var server = WireMockServer.Start();
        var url = server.Urls[0];
        server.Stop();

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var outcome = await client.ProvisionAsync(url, "tok", "Acme", "acme", CancellationToken.None);
        await Assert.That(outcome.StatusCode).IsEqualTo(0);
        await Assert.That(outcome.Body).IsNull();
    }

    // The key is read off the process-wide static rather than threaded through
    // OfferCreateAsync -> provisioner -> client: it is per-run state, the same shape CliTelemetry
    // already has everywhere, and null by construction whenever telemetry is off.
    [Test]
    public async Task ProvisionAsync_carries_the_minted_join_key_on_the_wire() {
        using var tmp = new TempDir();
        CliTelemetry.Reset();
        SetupJoin.Reset();
        CliTelemetry.TestSink = [];
        CliTelemetry.Initialize("setup", null, loggedIn: false, new ConfigRoot(tmp.Path));
        var key = SetupJoin.Mint();

        try {
            using var server = WireMockServer.Start();
            server.Given(Request.Create().WithPath("/api/signup/provision").UsingPost())
                .RespondWith(Response.Create().WithStatusCode(202)
                    .WithBody("""{"slug":"acme","state":"provisioning"}""")
                    .WithHeader("Content-Type", "application/json"));

            using var http = new HttpClient();
            await new TenantProvisioningClient(http)
                .ProvisionAsync(server.Urls[0], "tok", "Acme Inc", "acme", CancellationToken.None);

            var log  = server.FindLogEntries(Request.Create().WithPath("/api/signup/provision").UsingPost());
            var body = JsonNode.Parse(log[0].RequestMessage.Body!)!;

            await Assert.That(key).IsNotNull();
            await Assert.That(body["joinId"]!.GetValue<string>()).IsEqualTo(key);
        } finally {
            CliTelemetry.Reset();
            SetupJoin.Reset();
        }
    }

    [Test]
    public async Task ProvisionAsync_sends_no_joinId_when_telemetry_is_off() {
        CliTelemetry.Reset();
        SetupJoin.Reset();

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/provision").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202)
                .WithBody("""{"slug":"acme","state":"provisioning"}""")
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        await new TenantProvisioningClient(http)
            .ProvisionAsync(server.Urls[0], "tok", "Acme Inc", "acme", CancellationToken.None);

        var log = server.FindLogEntries(Request.Create().WithPath("/api/signup/provision").UsingPost());
        await Assert.That(log[0].RequestMessage.Body!).DoesNotContain("joinId");
    }
}
