using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// The rendered agent's permission request recovers from a 401 and resends. The leg is a long poll
/// the user answers by hand, so the token it opened with can be refused before an answer arrives;
/// ending on the first 401 abandons a decision the agent is blocked on, and Claude reads the hook's
/// failure as its result.
/// </summary>
[NotInParallel]
public class PermissionRequestRecoveryTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    string Url => _server.Url!;

    [Test]
    public async Task A_refused_token_is_rotated_and_the_permission_request_resent() {
        // The provider memo is process-wide, so a peer test's answer would otherwise decide this
        // one's: a cached None sends no bearer, and neither stub below would match.
        HttpClientExtensions.ResetProviderCacheForTesting();

        await AuthenticateAsync("tok_1");

        _server.Given(Request.Create().WithPath("/auth/refresh").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"tok_2","expires_in":3600}"""));

        _server.Given(Request.Create().WithPath("/hooks/permission-request").UsingPost()
                    .WithHeader("Authorization", "Bearer tok_1"))
            .RespondWith(Response.Create().WithStatusCode(401));

        _server.Given(Request.Create().WithPath("/hooks/permission-request").UsingPost()
                    .WithHeader("Authorization", "Bearer tok_2"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"decision":"allow"}"""));

        using var rendered = EnvScope.Exclusive("KCAP_RENDERED_AGENT", "1");
        using var noDaemon = EnvScope.Exclusive("KCAP_DAEMON_URL", null);

        var stdout = new StringWriter();
        var exit   = await CommandAsync().Handle(Payload, selfHealWatcher: false, stdout);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout.ToString()).Contains("allow");

        var posts = _server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/permission-request");
        await Assert.That(posts).IsEqualTo(2)
            .Because("the refused attempt and the resend after the rotation");
    }

    const string Payload =
        """
        {"session_id":"019e0322-05fc-7570-be65-75719c3ea861","tool_name":"Bash",
         "tool_input":{"command":"ls"},"cwd":"/tmp"}
        """;

    PermissionRequestCommand CommandAsync() =>
        new(Config.Root, Resolutions.At(Url, Config.Root));

    async Task AuthenticateAsync(string accessToken) {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"provider":"{{AuthProvider.GitHubApp}}"}"""));

        await new TokenStore(Config.Root).SaveAsync(
            Resolutions.At(Url, Config.Root).Name,
            new StoredTokens {
                AccessToken    = accessToken,
                ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
                GitHubUsername = "alice",
                Provider       = AuthProvider.GitHubApp,
                ServerUrl      = Url,
            });
    }
}
