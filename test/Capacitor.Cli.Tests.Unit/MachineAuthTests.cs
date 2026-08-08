using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Machine-credential authentication for headless runners.
///
/// <para>These tests deliberately go through the REAL HTTP exchange against a stub server rather than
/// asserting shapes around it. The immediately preceding work on this feature shipped a `kcap machine`
/// whose every subcommand threw before a request left the process, with a clean build, sixteen green
/// tests and a clean review — because every test exercised help text. The thing that has to work here is
/// the exchange, so the exchange is what is tested, including the wire format, which is precisely what
/// would be silently wrong.</para>
///
/// <para><c>[NotInParallel]</c> throughout: these manipulate process-wide environment variables and the
/// provider/token caches are process-wide statics.</para>
/// </summary>
[NotInParallel]
public class MachineAuthTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() {
        _server.Stop();
        Clear();
    }

    static void Clear() {
        Environment.SetEnvironmentVariable(MachineAuth.ClientIdVar, null);
        Environment.SetEnvironmentVariable(MachineAuth.ClientSecretVar, null);
        Environment.SetEnvironmentVariable("KCAP_WORKOS_TOKEN_URL", null);
        MachineTokenProvider.ResetForTesting();
        HttpClientExtensions.ResetProviderCacheForTesting();
    }

    void UseStubTokenEndpoint() =>
        Environment.SetEnvironmentVariable("KCAP_WORKOS_TOKEN_URL", $"{_server.Urls[0]}/oauth2/token");

    void StubToken(string token, int expiresIn = 3600) =>
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"access_token\":\"{token}\",\"expires_in\":{expiresIn}}}"));

    // ── Credential reading ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Both_variables_present_reads_the_credential() {
        Clear();
        Environment.SetEnvironmentVariable(MachineAuth.ClientIdVar, "client_01ABC");
        Environment.SetEnvironmentVariable(MachineAuth.ClientSecretVar, "sekrit");

        await Assert.That(MachineAuth.Intended).IsTrue();

        var credential = MachineAuth.TryRead(out var problem);

        await Assert.That(problem).IsNull();
        await Assert.That(credential!.ClientId).IsEqualTo("client_01ABC");
        await Assert.That(credential.ClientSecret).IsEqualTo("sekrit");
    }

    /// <summary>
    /// A half-configured runner must be told WHICH half is missing. Silently falling back would advise
    /// `kcap login` — impossible on a runner with no browser and no profile.
    /// </summary>
    [Test]
    [Arguments("client_01ABC", null, "KCAP_CLIENT_SECRET")]
    [Arguments(null, "sekrit", "KCAP_CLIENT_ID")]
    public async Task One_variable_present_is_reported_as_a_problem_naming_the_missing_one(
            string? id, string? secret, string expectedInProblem) {
        Clear();
        Environment.SetEnvironmentVariable(MachineAuth.ClientIdVar, id);
        Environment.SetEnvironmentVariable(MachineAuth.ClientSecretVar, secret);

        await Assert.That(MachineAuth.Intended).IsTrue()
            .Because("machine auth was clearly intended, so it must be diagnosed rather than skipped");

        var credential = MachineAuth.TryRead(out var problem);

        await Assert.That(credential).IsNull();
        await Assert.That(problem).IsNotNull();
        await Assert.That(problem!).Contains(expectedInProblem);
    }

    [Test]
    public async Task Neither_variable_present_is_not_machine_auth_at_all() {
        Clear();

        await Assert.That(MachineAuth.Intended).IsFalse();
        await Assert.That(MachineAuth.TryRead(out var problem)).IsNull();
        await Assert.That(problem).IsNull()
            .Because("an ordinary interactive user is not a misconfigured machine");
    }

    // ── The exchange itself ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The load-bearing test: a real POST, and the WIRE FORMAT asserted. Verified against the live
    /// WorkOS endpoint too, which answers this exact form with a credential rejection rather than
    /// `unsupported_grant_type`.
    /// </summary>
    [Test]
    public async Task Minting_posts_client_credentials_form_and_returns_the_token() {
        Clear();
        UseStubTokenEndpoint();
        StubToken("tok_minted");

        var token = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "sekrit"), rejectedToken: null, CancellationToken.None);

        await Assert.That(token).IsEqualTo("tok_minted");

        var requests = _server.LogEntries.ToList();

        await Assert.That(requests.Count).IsEqualTo(1);

        var body = requests[0].RequestMessage.Body ?? "";

        await Assert.That(body).Contains("grant_type=client_credentials");
        await Assert.That(body).Contains("client_id=client_01ABC");
        await Assert.That(body).Contains("client_secret=sekrit");
    }

    /// <summary>Second call reuses the cached token — no second mint.</summary>
    [Test]
    public async Task A_cached_token_is_reused_without_a_second_request() {
        Clear();
        UseStubTokenEndpoint();
        StubToken("tok_cached");

        var credential = new MachineCredential("client_01ABC", "sekrit");

        await MachineTokenProvider.GetTokenAsync(credential, null, CancellationToken.None);
        var second = await MachineTokenProvider.GetTokenAsync(credential, null, CancellationToken.None);

        await Assert.That(second).IsEqualTo("tok_cached");
        await Assert.That(_server.LogEntries.Count()).IsEqualTo(1)
            .Because("the whole point of the in-memory cache is not re-minting per call");
    }

    /// <summary>
    /// A 401 from the server comes back as `rejectedToken`, and that must force a re-mint. Without it a
    /// revoked-then-reissued credential would keep serving the dead token until its clock ran out.
    /// </summary>
    [Test]
    public async Task A_rejected_token_forces_a_fresh_mint() {
        Clear();
        UseStubTokenEndpoint();
        StubToken("tok_first");

        var credential = new MachineCredential("client_01ABC", "sekrit");
        var first      = await MachineTokenProvider.GetTokenAsync(credential, null, CancellationToken.None);

        await Assert.That(first).IsEqualTo("tok_first");

        var refreshed = await MachineTokenProvider.GetTokenAsync(credential, rejectedToken: first, CancellationToken.None);

        await Assert.That(refreshed).IsNotNull();
        await Assert.That(_server.LogEntries.Count()).IsEqualTo(2)
            .Because("the cached token was the one the server refused, so it had to be re-minted");
    }

    /// <summary>
    /// A rejection reports a problem — and the problem must NOT carry the secret. A token endpoint's
    /// error body is attacker-influenced and can reflect the request, which contains the secret.
    /// </summary>
    [Test]
    public async Task A_rejected_credential_reports_a_problem_without_leaking_the_secret() {
        Clear();
        UseStubTokenEndpoint();
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithHeader("Content-Type", "application/json")
                // A hostile/naive endpoint reflecting the request back at us.
                .WithBody("{\"error\":\"unauthorized\",\"echo\":\"client_secret=hunter2-the-secret\"}"));

        var token = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "hunter2-the-secret"), null, CancellationToken.None);

        await Assert.That(token).IsNull();
        await Assert.That(MachineTokenProvider.Problem).IsNotNull();
        await Assert.That(MachineTokenProvider.Problem!).Contains("401");
        await Assert.That(MachineTokenProvider.Problem!).DoesNotContain("hunter2-the-secret")
            .Because("the response body is never echoed — it can reflect the credential");
    }

    /// <summary>Success with no access_token must fail, not hand back an empty bearer.</summary>
    [Test]
    public async Task A_success_response_with_no_token_is_a_failure() {
        Clear();
        UseStubTokenEndpoint();
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("{\"expires_in\":3600}"));

        var token = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(token).IsNull();
        await Assert.That(MachineTokenProvider.Problem!).Contains("no access_token");
    }

    // ── The wiring: does an authenticated client actually carry the bearer? ─────────────────────

    /// <summary>
    /// End-to-end through the client-construction choke point every authenticated CLI call uses. This is
    /// the test whose absence let the last iteration of this feature ship completely non-functional: it
    /// is the only one that proves the branch is REACHED and the header attached.
    /// </summary>
    [Test]
    public async Task An_authenticated_client_carries_the_minted_bearer_with_no_profile_present() {
        Clear();
        UseStubTokenEndpoint();
        StubToken("tok_wired");

        // A runner's server is discovered over unauthenticated /auth/config — no profile, no token store.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"provider\":\"workos\",\"client_id\":\"client_01TENANT\",\"authkit_domain\":\"\",\"organization_id\":\"org_01T\"}"));

        Environment.SetEnvironmentVariable(MachineAuth.ClientIdVar, "client_01ABC");
        Environment.SetEnvironmentVariable(MachineAuth.ClientSecretVar, "sekrit");

        var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(_server.Urls[0]);

        await Assert.That(status).IsEqualTo(AuthStatus.Ok);
        await Assert.That(client.DefaultRequestHeaders.Authorization).IsNotNull();
        await Assert.That(client.DefaultRequestHeaders.Authorization!.Scheme).IsEqualTo("Bearer");
        await Assert.That(client.DefaultRequestHeaders.Authorization!.Parameter).IsEqualTo("tok_wired");
    }

    /// <summary>
    /// The same path with a half-configured credential reports NotAuthenticated rather than silently
    /// producing an unauthenticated client that would 401 with no explanation.
    /// </summary>
    [Test]
    public async Task A_half_configured_runner_reports_not_authenticated() {
        Clear();
        UseStubTokenEndpoint();

        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"provider\":\"workos\",\"client_id\":\"client_01TENANT\",\"authkit_domain\":\"\",\"organization_id\":\"org_01T\"}"));

        Environment.SetEnvironmentVariable(MachineAuth.ClientIdVar, "client_01ABC"); // secret missing

        var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(_server.Urls[0]);

        await Assert.That(status).IsEqualTo(AuthStatus.NotAuthenticated);
        await Assert.That(client.DefaultRequestHeaders.Authorization).IsNull();
    }
}
