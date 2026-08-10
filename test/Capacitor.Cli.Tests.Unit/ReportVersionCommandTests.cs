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
/// `kcap update` installs a new binary. Its whole purpose is to make ONE authenticated,
/// side-effect-free GET through <see cref="HttpClientExtensions.CreateClientWithAuthStatusAsync"/>
/// — the single choke point that attaches <see cref="HttpClientExtensions.CliVersionHeader"/> — so
/// the server's version observer sees the new version immediately instead of waiting for the next
/// incidental request. It reuses <see cref="WhoamiCommand.ProbePath"/> (the same read-only
/// identity GET <c>kcap whoami</c> uses) rather than any write-side-effecting endpoint. It must
/// never surface an error: not-authenticated makes no request at all, and a server fault, a slow
/// server, or no server configured at all still returns 0.
///
/// <para><c>[NotInParallel(nameof(TokenStoreProfileTests))]</c>: shares
/// <c>ObservationHeaderTests</c>'s serialization key — these tests touch the process-wide
/// <see cref="AppConfig.ResolvedProfile"/> static and the shared <c>KCAP_CONFIG_DIR</c>
/// config.json that other classes in this assembly also read and write.</para>
/// </summary>
[NotInParallel(nameof(TokenStoreProfileTests))]
public class ReportVersionCommandTests : IDisposable {
    const string ProbePath = WhoamiCommand.ProbePath;

    readonly WireMockServer _server = WireMockServer.Start();

    [Before(Test)]
    public void Cleanup() {
        AppConfig.ResetResolvedStateForTesting();
        HttpClientExtensions.ResetProviderCacheForTesting();
        Environment.SetEnvironmentVariable("KCAP_URL", null);

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

    // ── Authenticated: exactly one GET, carrying the version header, no write side effect ──────

    [Test]
    public async Task Authenticated_MakesOneGetCarryingTheVersionHeader() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        await SeedValidTokenAsync("report-version-ok");

        var result = await ReportVersionCommand.HandleAsync(_server.Urls[0]);

        await Assert.That(result).IsEqualTo(0);

        var requests = _server.LogEntries.Where(e => e.RequestMessage.Path == ProbePath).ToList();
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].RequestMessage.Method).IsEqualTo("GET");
        await Assert.That(requests[0].RequestMessage.Headers![HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
    }

    /// <summary>
    /// An <c>Auth:Provider=None</c> tenant: no bearer token exists (there is nothing to log
    /// into), but the request still authenticates via the server's synthetic principal, so the
    /// middleware still observes it — <see cref="AuthStatus.NoAuthRequired"/> must proceed exactly
    /// like <see cref="AuthStatus.Ok"/>, not be treated as "not authenticated".
    /// </summary>
    [Test]
    public async Task NoAuthTenant_MakesOneGetCarryingTheVersionHeader() {
        StubDiscovery("None");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var result = await ReportVersionCommand.HandleAsync(_server.Urls[0]);

        await Assert.That(result).IsEqualTo(0);

        var requests = _server.LogEntries.Where(e => e.RequestMessage.Path == ProbePath).ToList();
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].RequestMessage.Headers![HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
    }

    // ── Not authenticated: no request at all, still returns 0 ─────────────────────────────────

    [Test]
    public async Task NotAuthenticated_MakesNoRequest_AndReturnsZero() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        // No token stored, no resolved profile — AuthStatus resolves to NotAuthenticated.
        var result = await ReportVersionCommand.HandleAsync(_server.Urls[0]);

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == ProbePath)).IsFalse();
    }

    [Test]
    public async Task ExpiredToken_MakesNoRequest_AndReturnsZero() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
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
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == ProbePath)).IsFalse();
    }

    // ── Server-side failures: fail-open, never throws ──────────────────────────────────────────

    [Test]
    public async Task ServerErrorResponse_StillReturnsZero() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        await SeedValidTokenAsync("report-version-500");

        var result = await ReportVersionCommand.HandleAsync(_server.Urls[0]);

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task SlowServer_StillReturnsZero_WithinItsOwnBudget() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(10)));

        await SeedValidTokenAsync("report-version-slow");

        var sw     = Stopwatch.StartNew();
        var result = await ReportVersionCommand.HandleAsync(_server.Urls[0]);
        sw.Stop();

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(8));
    }

    /// <summary>
    /// Regression for the wrong-host bug: <c>CreateClientWithAuthStatusAsync</c>'s own baseUrl
    /// fallback also consults <c>KCAP_URL</c>, so the probe URL must be built from that SAME
    /// resolved value — not recomputed without it — or the client authenticates against one host
    /// while the GET (carrying the bearer token) goes to another.
    /// </summary>
    [Test]
    public async Task NoBaseUrl_FallsBackToKcapUrlEnvVar_NotLocalhost() {
        StubDiscovery("None");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var previous = Environment.GetEnvironmentVariable("KCAP_URL");
        Environment.SetEnvironmentVariable("KCAP_URL", _server.Urls[0]);
        try {
            var result = await ReportVersionCommand.HandleAsync(null);

            await Assert.That(result).IsEqualTo(0);

            var requests = _server.LogEntries.Where(e => e.RequestMessage.Path == ProbePath).ToList();
            await Assert.That(requests.Count).IsEqualTo(1);
        } finally {
            Environment.SetEnvironmentVariable("KCAP_URL", previous);
        }
    }

    /// <summary>Discovery itself has no deadline; the command's own budget must still bound it.</summary>
    [Test]
    public async Task SlowDiscovery_StillReturnsZero_WithinBudget() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"github_app"}""")
                .WithDelay(TimeSpan.FromSeconds(10)));

        await SeedValidTokenAsync("report-version-slow-discovery");

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

    /// <summary>
    /// Mirrors <c>Program.cs</c>'s dispatch: <c>report-version</c> is in <c>offlineCommands</c>,
    /// so a host with no server configured at all reaches this command with a null
    /// <c>baseUrl</c> — it must still hit this command's own fail-open logic and return 0 silently
    /// rather than the generic "No server configured" exit 1 the pre-dispatch gate would
    /// otherwise produce for any command not on that list.
    /// </summary>
    [Test]
    public async Task NoServerConfigured_MakesNoRequest_AndReturnsZero() {
        // No AppConfig.SetResolvedState, no KCAP_URL: CreateClientWithAuthStatusAsync falls back
        // to its hardcoded "http://localhost:5108" default, which nothing is listening on here.
        var result = await ReportVersionCommand.HandleAsync(null);

        await Assert.That(result).IsEqualTo(0);
    }
}
