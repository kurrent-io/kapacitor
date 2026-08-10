using System.Diagnostics;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The hidden <c>kcap report-version</c> command, invoked by the npm wrapper right after
/// `kcap update` installs a new binary. Its whole purpose is to make ONE authenticated request
/// through <see cref="HttpClientExtensions.CreateClientWithAuthStatusAsync"/> — the single choke
/// point that attaches <see cref="HttpClientExtensions.CliVersionHeader"/> — so the server's
/// version observer sees the new version immediately instead of waiting for the next incidental
/// request. It must never surface an error: not-authenticated makes no request at all, and a
/// server fault or a slow server still returns 0 within its own request budget.
///
/// <para><c>[NotInParallel(nameof(TokenStoreProfileTests))]</c>: shares
/// <c>ObservationHeaderTests</c>'s serialization key — these tests touch the process-wide
/// <see cref="AppConfig.ResolvedProfile"/> static and the shared <c>KCAP_CONFIG_DIR</c>
/// config.json that other classes in this assembly also read and write.</para>
/// </summary>
[NotInParallel(nameof(TokenStoreProfileTests))]
public class ReportVersionCommandTests : IDisposable {
    const string ReportPath = "/api/users/me/cli-setup";

    readonly WireMockServer _server = WireMockServer.Start();

    [Before(Test)]
    public void Cleanup() {
        AppConfig.ResetResolvedStateForTesting();
        HttpClientExtensions.ResetProviderCacheForTesting();

        var cfg = AppConfig.GetConfigPath();
        if (File.Exists(cfg)) File.Delete(cfg);
    }

    public void Dispose() {
        _server.Stop();
        AppConfig.ResetResolvedStateForTesting();
        HttpClientExtensions.ResetProviderCacheForTesting();
    }

    void StubDiscovery(string provider) =>
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"provider":"{{provider}}"}"""));

    async Task SeedValidTokenAsync(string profileName) {
        AppConfig.SetResolvedState(_server.Urls[0], profileName, new Profile());
        await TokenStore.SaveAsync(profileName, new StoredTokens {
            AccessToken    = "tok-" + profileName,
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = _server.Urls[0],
        });
    }

    // ── Authenticated: exactly one request, carrying the version header ───────────────────────

    [Test]
    public async Task Authenticated_MakesOneRequestCarryingTheVersionHeader() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ReportPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        await SeedValidTokenAsync("report-version-ok");

        var result = await ReportVersionCommand.HandleAsync(_server.Urls[0]);

        await Assert.That(result).IsEqualTo(0);

        var requests = _server.LogEntries.Where(e => e.RequestMessage.Path == ReportPath).ToList();
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].RequestMessage.Headers![HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
    }

    // ── Not authenticated: no request at all, still returns 0 ─────────────────────────────────

    [Test]
    public async Task NotAuthenticated_MakesNoRequest_AndReturnsZero() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ReportPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        // No token stored, no resolved profile — AuthStatus resolves to NotAuthenticated.
        var result = await ReportVersionCommand.HandleAsync(_server.Urls[0]);

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == ReportPath)).IsFalse();
    }

    [Test]
    public async Task ExpiredToken_MakesNoRequest_AndReturnsZero() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ReportPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        const string profileName = "report-version-expired";
        AppConfig.SetResolvedState(_server.Urls[0], profileName, new Profile());
        await TokenStore.SaveAsync(profileName, new StoredTokens {
            AccessToken    = "tok-expired",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(-1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = _server.Urls[0],
        });

        var result = await ReportVersionCommand.HandleAsync(_server.Urls[0]);

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == ReportPath)).IsFalse();
    }

    // ── Server-side failures: fail-open, never throws ──────────────────────────────────────────

    [Test]
    public async Task ServerErrorResponse_StillReturnsZero() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ReportPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        await SeedValidTokenAsync("report-version-500");

        var result = await ReportVersionCommand.HandleAsync(_server.Urls[0]);

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task SlowServer_StillReturnsZero_WithinItsOwnBudget() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ReportPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(10)));

        await SeedValidTokenAsync("report-version-slow");

        var sw     = Stopwatch.StartNew();
        var result = await ReportVersionCommand.HandleAsync(_server.Urls[0]);
        sw.Stop();

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(8));
    }

    [Test]
    public async Task UnreachableServer_StillReturnsZero() {
        // No discovery stub, no listener at all on this port — DiscoverProviderAsync's own
        // catch falls back to local tokens, finds none, resolves NotAuthenticated; even if it
        // somehow resolved Ok, the command's own try/catch must still swallow the failure.
        var result = await ReportVersionCommand.HandleAsync("http://127.0.0.1:1");

        await Assert.That(result).IsEqualTo(0);
    }
}
