using System.Net;
using System.Text;
using Capacitor.Cli.Core.Auth;
using NSubstitute;
using DiscoveryResult = Capacitor.Cli.Core.Auth.DiscoveryResult;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Records every call instead of writing to Console — the test seam for asserting call shape.</summary>
sealed class RecordingAuthProgress : IAuthProgress {
    public List<string>              Notices         = [];
    public List<string>               Errors          = [];
    public List<string>               BrowserOpenings = [];
    public List<(string Code, string Uri)> DeviceCodes = [];
    public int                        PollTicks;

    public void Notice(string message) => Notices.Add(message);
    public void Error(string message) => Errors.Add(message);
    public void BrowserOpening(string url) => BrowserOpenings.Add(url);
    public void DeviceCode(string code, string verificationUri) => DeviceCodes.Add((code, verificationUri));
    public void PollTick() => PollTicks++;
}

// Console redirection is process-global state; keep every test in this file serialized against
// each other (and against anything else asserting on captured stdout/stderr).
[NotInParallel]
public class AuthProgressTests {
    sealed class FakeGitHubDeviceHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Test]
    public async Task RunDeviceFlowAsync_reports_progress_through_the_sink_not_console() {
        var pollCount = 0;

        using var handler = new FakeGitHubDeviceHandler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("device/code")) {
                // Empty verification_uri: Process.Start throws synchronously on an empty file name,
                // so the best-effort browser-open never actually launches anything during the test.
                return JsonResponse("""{"device_code":"dc","user_code":"UC123","verification_uri":"","interval":0}""");
            }

            pollCount++;

            return pollCount < 3
                ? JsonResponse("""{"error":"authorization_pending"}""")
                : JsonResponse("""{"access_token":"tok"}""");
        });
        using var http = new HttpClient(handler);

        var progress = new RecordingAuthProgress();

        var originalOut = Console.Out;
        var captured    = new StringWriter();
        Console.SetOut(captured);

        string? token;

        try {
            token = await OAuthLoginFlow.RunDeviceFlowAsync(http, "client_id", progress: progress);
        } finally {
            Console.SetOut(originalOut);
        }

        await Assert.That(token).IsEqualTo("tok");
        await Assert.That(progress.DeviceCodes).HasCount(1);
        // A successful clipboard copy (environment-dependent) appends a suffix to the code.
        await Assert.That(progress.DeviceCodes[0].Code).StartsWith("UC123");
        await Assert.That(progress.PollTicks).IsEqualTo(2); // 2 "authorization_pending" polls before success
        await Assert.That(progress.Notices).Contains(" done!");
        // Nothing reached Console — everything routed through the recording sink.
        await Assert.That(captured.ToString()).IsEmpty();
    }

    [Test]
    public async Task RunAsync_zero_tenant_headless_emits_through_progress_not_console() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

        var progress = new RecordingAuthProgress();

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var capturedOut = new StringWriter();
        var capturedErr = new StringWriter();
        Console.SetOut(capturedOut);
        Console.SetError(capturedErr);

        WorkOSDiscoveryOutcome outcome;

        try {
            outcome = await WorkOSDiscovery.RunAsync(
                "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
                proxy, Substitute.For<ITenantPicker>(),
                orglessLogin: () => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
                orgSwitch: (_, _) => Task.FromResult<WorkOSAuthResponse?>(null),
                progress: progress);
        } finally {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        await Assert.That(outcome.ExitCode).IsEqualTo(1);
        // Today's code writes this line to stderr — pinned so a future stream swap is deliberate.
        await Assert.That(progress.Errors).Contains("No Capacitor tenants are linked to your account. Ask your admin to invite you.");
        await Assert.That(capturedOut.ToString()).IsEmpty();
        await Assert.That(capturedErr.ToString()).IsEmpty();
    }

    [Test]
    public async Task ConsoleAuthProgress_DeviceCode_matches_todays_banner_lines() {
        var originalOut = Console.Out;
        var captured    = new StringWriter();
        Console.SetOut(captured);

        try {
            new ConsoleAuthProgress().DeviceCode("UC123", "https://github.com/login/device");
        } finally {
            Console.SetOut(originalOut);
        }

        await Assert.That(captured.ToString()).IsEqualTo(
            "  2. Enter the code: UC123" + Environment.NewLine
          + "  3. Approve access when GitHub asks." + Environment.NewLine
          + Environment.NewLine
          + "Waiting for you to authorize...");
    }

    [Test]
    public async Task ConsoleAuthProgress_BrowserOpening_matches_todays_notice_lines() {
        var originalOut = Console.Out;
        var captured    = new StringWriter();
        Console.SetOut(captured);

        try {
            new ConsoleAuthProgress().BrowserOpening("https://example.test/authorize");
        } finally {
            Console.SetOut(originalOut);
        }

        await Assert.That(captured.ToString()).IsEqualTo(
            "Opening browser for authentication..." + Environment.NewLine
          + "  If the browser doesn't open, visit: https://example.test/authorize" + Environment.NewLine);
    }

    [Test]
    public async Task ConsoleAuthProgress_PollTick_writes_dot_without_newline() {
        var originalOut = Console.Out;
        var captured    = new StringWriter();
        Console.SetOut(captured);

        try {
            new ConsoleAuthProgress().PollTick();
        } finally {
            Console.SetOut(originalOut);
        }

        await Assert.That(captured.ToString()).IsEqualTo(".");
    }
}
