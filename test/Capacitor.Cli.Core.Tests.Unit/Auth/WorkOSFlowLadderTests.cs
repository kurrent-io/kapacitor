using System.Net;
using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>AI-2052 — the WorkOS sign-in ladder: loopback, escape hatch, automatic fallback.</summary>
public class WorkOSFlowLadderTests {
    const string Device = """{"device_code":"dc","user_code":"WXYZ-1234","verification_uri":"https://signin.example/device","interval":0,"expires_in":900}""";

    static WireMockServer DeviceGrantServer(string authenticate = """{"access_token":"acc","refresh_token":"rt","organization_id":"org_a"}""") {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authorize/device").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Device));
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(authenticate));

        return server;
    }

    /// <summary>
    /// The load-bearing claim of §5's ladder. IsHeadless() is true for every SSH session, including
    /// Remote-SSH and `ssh -L` where loopback works — so letting it select would demote the largest
    /// developer population to typing a code.
    /// </summary>
    [Test]
    [Arguments(false, false, WorkOSFlow.Browser)]
    [Arguments(false, true,  WorkOSFlow.BrowserRemote)]
    [Arguments(true,  false, WorkOSFlow.Device)]
    [Arguments(true,  true,  WorkOSFlow.Device)]
    public async Task Only_an_explicit_request_skips_loopback(bool forceDevice, bool isHeadless, WorkOSFlow expected) =>
        await Assert.That(OAuthLoginFlow.ChooseWorkOSFlow(forceDevice, isHeadless)).IsEqualTo(expected);

    [Test]
    public async Task Offers_the_flag_instead_of_the_key_when_there_is_no_keyboard() {
        await Assert.That(OAuthLoginFlow.WorkOSBrowserNotice(WorkOSFlow.Browser, canWatchKeys: true)).Contains("Press d");
        await Assert.That(OAuthLoginFlow.WorkOSBrowserNotice(WorkOSFlow.Browser, canWatchKeys: false)).DoesNotContain("Press d");
        await Assert.That(OAuthLoginFlow.WorkOSBrowserNotice(WorkOSFlow.Browser, canWatchKeys: false)).Contains("--device");
        await Assert.That(OAuthLoginFlow.WorkOSBrowserNotice(WorkOSFlow.BrowserRemote, canWatchKeys: true)).Contains("remote session");
    }

    [Test]
    public async Task Explicit_device_request_never_opens_a_browser() {
        using var server  = DeviceGrantServer();
        using var http    = new HttpClient();
        var       browser = new FakeBrowser(_ => throw new InvalidOperationException("the browser must not be invoked"));

        var result = await OAuthLoginFlow.AcquireWorkOSAsync(
            http, "client_d", organizationId: null, forceDevice: true, browser,
            server.Urls[0], progress: new RecordingAuthProgress(), keys: ScriptedKeyWatcher.Blind());

        await Assert.That(result!.AccessToken).IsEqualTo("acc");
    }

    [Test]
    public async Task The_escape_hatch_abandons_the_browser_for_the_device_grant() {
        using var server   = DeviceGrantServer();
        using var http     = new HttpClient();
        var       keys     = new ScriptedKeyWatcher('d');
        var       progress = new RecordingAuthProgress();

        var result = await OAuthLoginFlow.AcquireWorkOSAsync(
            http, "client_d", organizationId: null, forceDevice: false, new HangingBrowser(),
            server.Urls[0], progress: progress, keys: keys);

        await Assert.That(result!.AccessToken).IsEqualTo("acc");
        await Assert.That(progress.DeviceCodes).Count().IsEqualTo(1);
    }

    /// <summary>
    /// The key is usually followed by a Return. Spectre's tenant picker runs next and would read the
    /// leftover as an answer, silently choosing the first tenant.
    /// </summary>
    [Test]
    public async Task Drains_what_is_still_buffered_before_handing_off() {
        using var server = DeviceGrantServer();
        using var http   = new HttpClient();
        var       keys   = new ScriptedKeyWatcher('d', '\r', '\n');

        await OAuthLoginFlow.AcquireWorkOSAsync(
            http, "client_d", organizationId: null, forceDevice: false, new HangingBrowser(),
            server.Urls[0], progress: new RecordingAuthProgress(), keys: keys);

        await Assert.That(keys.Drained).IsEqualTo(2);
        await Assert.That(keys.KeyAvailable).IsFalse();
    }

    /// <summary>Mirrors the GitHub arm: a browser flow that RAN and failed is an answer, not a reason
    /// to re-ask through another channel.</summary>
    [Test]
    public async Task A_cancelled_browser_sign_in_does_not_fall_through_to_the_device_grant() {
        using var server = DeviceGrantServer();
        using var http   = new HttpClient();

        var result = await OAuthLoginFlow.AcquireWorkOSAsync(
            http, "client_d", organizationId: null, forceDevice: false,
            FakeBrowser.NonSuccess(Duende.IdentityModel.OidcClient.Browser.BrowserResultType.UserCancel),
            server.Urls[0], progress: new RecordingAuthProgress(), keys: ScriptedKeyWatcher.Blind());

        await Assert.That(result).IsNull();
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path.Contains("authorize/device"))).IsFalse();
    }

    /// <summary>The third rung, and it is free: OAuthFlowTests pins that the bind failure propagates
    /// out of OidcClient rather than being folded into an error result.</summary>
    [Test]
    public async Task A_loopback_bind_failure_falls_through_to_the_device_grant() {
        using var server   = DeviceGrantServer();
        using var http     = new HttpClient();
        var       progress = new RecordingAuthProgress();

        var result = await OAuthLoginFlow.AcquireWorkOSAsync(
            http, "client_d", organizationId: null, forceDevice: false,
            new FakeBrowser(_ => throw new HttpListenerException(5, "Access is denied")),
            server.Urls[0], progress: progress, keys: ScriptedKeyWatcher.Blind());

        await Assert.That(result!.AccessToken).IsEqualTo("acc");
        await Assert.That(string.Join("\n", progress.Errors)).Contains("Could not bind loopback listener");
    }

    /// <summary>A caller cancel must not be mistaken for the escape hatch and rewarded with a device code.</summary>
    [Test]
    public async Task A_caller_cancel_propagates_rather_than_falling_through() {
        using var server = DeviceGrantServer();
        using var http   = new HttpClient();
        using var cts    = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await OAuthLoginFlow.AcquireWorkOSAsync(
                  http, "client_d", organizationId: null, forceDevice: false, new HangingBrowser(),
                  server.Urls[0], cts.Token, new RecordingAuthProgress(), ScriptedKeyWatcher.Blind()))
            .Throws<OperationCanceledException>();
    }
}
