using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The wire contract PR-2 (kcap-server) reads: every client built by
/// <see cref="HttpClientExtensions"/>'s single choke point, and the raw client
/// <see cref="WhoamiCommand"/> builds outside it, must carry the CLI's display version, and must
/// carry the update-check opt-out header if and only if the active profile turned it off. Absence
/// of the opt-out header on a version-carrying request is read by the server as "on" — so both the
/// present and the absent case are asserted here, not just the header's shape when it does appear.
///
/// <para>Keyed on the auth-discovery cache: <c>HttpClientExtensions</c> caches the first successful
/// <c>/auth/config</c> discovery for the whole process, so a stub here decides what a concurrent
/// test's stub returns.</para>
/// </summary>
[NotInParallel("AuthProviderDiscoveryCache")]
public class ObservationHeaderTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    [Before(Test)]
    public void Cleanup() {
        HttpClientExtensions.ResetProviderCacheForTesting();

    }

    public void Dispose() {
        _server.Stop();
        HttpClientExtensions.ResetProviderCacheForTesting();
    }

    void StubDiscovery(string provider = "None") =>
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"provider":"{{provider}}"}"""));

    // ── CreateClientCoreAsync (via CreateClientWithAuthStatusAsync) ────────────────────────────

    [Test]
    public async Task Client_always_carries_the_display_version_with_no_build_suffix() {
        StubDiscovery();

        var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(
            Config.Root, Resolutions.None(Config.Root), _server.Urls[0]);

        await Assert.That(status).IsEqualTo(AuthStatus.NoAuthRequired);
        await Assert.That(client.DefaultRequestHeaders.Contains(HttpClientExtensions.CliVersionHeader)).IsTrue();

        var value = client.DefaultRequestHeaders.GetValues(HttpClientExtensions.CliVersionHeader).Single();
        await Assert.That(value).IsEqualTo(CapacitorVersion.CurrentDisplay());
        await Assert.That(value).DoesNotContain("+");
    }

    [Test]
    public async Task Off_header_is_sent_when_the_active_profile_disabled_update_check() {
        StubDiscovery();
        var profiles = Resolutions.Of(new Profile { UpdateCheck = false }, "obs-headers-off", _server.Urls[0]);

        var (client, _) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(
            Config.Root, profiles, _server.Urls[0]);

        await Assert.That(client.DefaultRequestHeaders.Contains(HttpClientExtensions.UpdateCheckHeader)).IsTrue();
        await Assert.That(client.DefaultRequestHeaders.GetValues(HttpClientExtensions.UpdateCheckHeader).Single())
            .IsEqualTo(HttpClientExtensions.UpdateCheckOffValue);
    }

    /// <summary>
    /// The non-vacuous half of the pair above: update_check ON (the default) must NOT send the
    /// header at all — a server that treats absence as "on" would misread a stray "on" value sent
    /// defensively, so the implementation must omit it rather than send a truthy value.
    /// </summary>
    [Test]
    public async Task Off_header_is_absent_when_the_active_profile_has_update_check_on() {
        StubDiscovery();
        var profiles = Resolutions.Of(new Profile { UpdateCheck = true }, "obs-headers-on", _server.Urls[0]);

        var (client, _) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(
            Config.Root, profiles, _server.Urls[0]);

        await Assert.That(client.DefaultRequestHeaders.Contains(HttpClientExtensions.UpdateCheckHeader)).IsFalse();
    }

    [Test]
    public async Task Off_header_is_absent_when_no_profile_is_resolved_at_all() {
        StubDiscovery();
        // Nothing resolved and no config.json under this root, so the effective profile is the
        // built-in default, whose update_check defaults to true.
        var (client, _) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(
            Config.Root, Resolutions.None(Config.Root), _server.Urls[0]);

        await Assert.That(client.DefaultRequestHeaders.Contains(HttpClientExtensions.UpdateCheckHeader)).IsFalse();
    }

    // ── WhoamiCommand.ProbeAsync (the raw client that bypasses the choke point) ────────────────

    /// <summary>
    /// ProbeAsync is private (deliberately — it must send exactly the token whoami printed, with no
    /// refresh). Driving it through the real <see cref="WhoamiCommand.HandleAsync"/> and capturing
    /// the actual request WireMock received is the only way to prove the headers reached the wire,
    /// as opposed to merely being attached to some client that HandleAsync doesn't end up using.
    /// </summary>
    [Test]
    public async Task Whoami_probe_request_carries_both_headers_when_update_check_is_off() {
        const string profileName = "obs-headers-whoami-off";
        StubDiscovery(provider: "github_app");
        _server.Given(Request.Create().WithPath(WhoamiCommand.ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var profiles = Resolutions.Of(new Profile { UpdateCheck = false }, profileName, _server.Urls[0]);
        await new TokenStore(Config.Root).SaveAsync(profileName, new StoredTokens {
            AccessToken    = "tok-whoami-off",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = _server.Urls[0],
        });

        // The one resolution steers both: the header preference and the profile whose token is read.
        await new WhoamiCommand(Config.Root, profiles).HandleAsync();

        var probe = _server.LogEntries.Single(e => e.RequestMessage.Path == WhoamiCommand.ProbePath);

        await Assert.That(probe.RequestMessage.Headers![HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
        await Assert.That(probe.RequestMessage.Headers![HttpClientExtensions.UpdateCheckHeader].Single())
            .IsEqualTo(HttpClientExtensions.UpdateCheckOffValue);
    }

    /// <summary>Same probe, opposite preference: the off-header must not reach the wire either.</summary>
    [Test]
    public async Task Whoami_probe_request_omits_the_off_header_when_update_check_is_on() {
        const string profileName = "obs-headers-whoami-on";
        StubDiscovery(provider: "github_app");
        _server.Given(Request.Create().WithPath(WhoamiCommand.ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var profiles = Resolutions.Of(new Profile { UpdateCheck = true }, profileName, _server.Urls[0]);
        await new TokenStore(Config.Root).SaveAsync(profileName, new StoredTokens {
            AccessToken    = "tok-whoami-on",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = _server.Urls[0],
        });

        // The one resolution steers both: the header preference and the profile whose token is read.
        await new WhoamiCommand(Config.Root, profiles).HandleAsync();

        var probe = _server.LogEntries.Single(e => e.RequestMessage.Path == WhoamiCommand.ProbePath);

        await Assert.That(probe.RequestMessage.Headers![HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
        await Assert.That(probe.RequestMessage.Headers!.ContainsKey(HttpClientExtensions.UpdateCheckHeader)).IsFalse();
    }
}
