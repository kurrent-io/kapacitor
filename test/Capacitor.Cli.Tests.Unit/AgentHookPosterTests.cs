using Capacitor.Cli.Commands;
using Capacitor.Cli.Core; using Capacitor.Cli.Core.Auth;
using Capacitor.Tests.Helpers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Tests for the shared agent-hook recording POST. This is the seam the non-Claude
/// hooks (Codex, Gemini, Copilot, Pi, Kiro, OpenCode) delegate to. Its job is to SKIP a POST
/// that would 401 because auth has lapsed — reporting <see cref="HookPostOutcome.AuthLapsed"/>
/// without touching stderr or the server — while leaving the authenticated success path and
/// the real-failure path (stderr + <see cref="HookPostOutcome.Failed"/>) unchanged.
///
/// The (client, status) factory is injected so the auth outcome is controlled directly and no
/// token store or /auth/config discovery is needed; the POST itself goes to a WireMock server.
/// </summary>
public class AgentHookPosterTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static Func<Task<(HttpClient, AuthStatus)>> Factory(AuthStatus status)
        => () => Task.FromResult((new HttpClient(), status));

    [Test]
    public async Task Expired_auth_skips_the_post_and_reports_AuthLapsed() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var outcome = await AgentHookPoster.PostAsync(
            Factory(AuthStatus.Expired), _server.Url!, "session-start/codex", "{}", "codex-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.AuthLapsed);

        // The doomed POST must never be sent.
        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NotAuthenticated_skips_the_post_and_reports_AuthLapsed() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/gemini").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var outcome = await AgentHookPoster.PostAsync(
            Factory(AuthStatus.NotAuthenticated), _server.Url!, "session-start/gemini", "{}", "gemini-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.AuthLapsed);

        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/gemini").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Ok_auth_posts_the_body_and_reports_Posted() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/pi").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var outcome = await AgentHookPoster.PostAsync(
            Factory(AuthStatus.Ok), _server.Url!, "session-start/pi", """{"hello":"world"}""", "pi-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Posted);

        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/pi").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(1);
        await Assert.That(hits[0].RequestMessage.Body).IsEqualTo("""{"hello":"world"}""");
    }

    [Test]
    public async Task NoAuthRequired_posts_normally() {
        // "None" provider → the client is usable as-is; behave exactly like authenticated.
        _server.Given(Request.Create().WithPath("/hooks/session-start/opencode").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var outcome = await AgentHookPoster.PostAsync(
            Factory(AuthStatus.NoAuthRequired), _server.Url!, "session-start/opencode", "{}", "opencode-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Posted);

        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/opencode").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Server_error_reports_Failed() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        var outcome = await AgentHookPoster.PostAsync(
            Factory(AuthStatus.Ok), _server.Url!, "session-start/kiro", "{}", "kiro-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Failed);
    }

    /// <summary>
    /// A 401 must stay <see cref="HookPostOutcome.Failed"/> — the outcome drives the caller's exit
    /// code and is not part of this change — while the line the user actually reads names the fix.
    /// These vendors have no user-facing stdout channel (their stdout is a handshake contract the
    /// vendor parses), so stderr is the only place a nudge can go.
    /// </summary>
    // Globally sequential rather than keyed. "ConsoleErrorRedirect" has no other member, so it
    // serialized this Console.Error capture against nothing: Server_error_reports_Failed below
    // writes to Console.Error with no serialization at all and could land inside this window, and
    // the classic save/restore interleave with this suite's other Console.SetError sites could
    // leave Console.Error pointing at an abandoned StringWriter. Bare NotInParallel serializes
    // against every other bare-NotInParallel test in the assembly, which is what this needs.
    [Test, NotInParallel]
    public async Task Unauthorized_reports_Failed_and_names_kcap_login_on_stderr() {
        _server.Given(Request.Create().WithPath("/hooks/stop/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));

        using var capture = ConsoleOutput.StartErrorCapture("\n");
        HookPostOutcome outcome;

        outcome = await AgentHookPoster.PostAsync(
            Factory(AuthStatus.Ok), _server.Url!, "stop/codex", "{}", "codex-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Failed);
        await Assert.That(capture.GetCapturedError().Trim()).IsEqualTo(
            AuthRejectionNotice.VendorStderrLine("codex-hook", "stop/codex", 401));
    }

    [Test]
    public async Task IsAuthLapsed_is_true_only_for_expired_or_unauthenticated() {
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.Expired)).IsTrue();
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.NotAuthenticated)).IsTrue();
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.Ok)).IsFalse();
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.NoAuthRequired)).IsFalse();
    }

    [Test]
    public async Task PostOrSpool_on_auth_lapse_spools_and_returns_Spooled() {
        var dir = Path.Combine(Path.GetTempPath(), $"kcap-poster-{Guid.NewGuid():N}");
        try {
            var spool = new HookSpool(dir);
            var outcome = await AgentHookPoster.PostOrSpoolAsync(
                () => Task.FromResult<(HttpClient, AuthStatus)>((new HttpClient(), AuthStatus.Expired)),
                "http://localhost:1", "session-start/kiro", """{"session_id":"x"}""",
                "kiro-hook", spool, sessionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", route: "session-start/kiro");

            await Assert.That(outcome).IsEqualTo(HookPostOutcome.Spooled);
            await Assert.That(spool.HasBacklog("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).IsTrue();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task PostOrSpool_on_success_returns_Posted_and_spools_nothing() {
        var dir = Path.Combine(Path.GetTempPath(), $"kcap-poster-{Guid.NewGuid():N}");
        try {
            var spool = new HookSpool(dir);
            using var handler = new StubHandler(System.Net.HttpStatusCode.OK); // 200
            var outcome = await AgentHookPoster.PostOrSpoolAsync(
                () => Task.FromResult<(HttpClient, AuthStatus)>((new HttpClient(handler), AuthStatus.Ok)),
                "http://server", "session-start/kiro", """{"session_id":"x"}""",
                "kiro-hook", spool, sessionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", route: "session-start/kiro");

            await Assert.That(outcome).IsEqualTo(HookPostOutcome.Posted);
            await Assert.That(spool.HasBacklog("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).IsFalse();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

sealed class StubHandler(System.Net.HttpStatusCode code) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(code));
}
