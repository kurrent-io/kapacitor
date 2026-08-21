using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>
/// The chain driven by a REAL BROWSER, which is the one thing
/// <see cref="JoinChainLiveTests"/> cannot do.
///
/// <para><b>Why this exists.</b> That test drives the chain with <c>HttpClient</c> and a
/// <c>CookieContainer</c>. Two of this feature's load-bearing claims are invisible to it:</para>
/// <list type="number">
/// <item><c>HttpClient</c> does not implement <c>SameSite</c> AT ALL, so "a top-level GET navigation
/// is what makes <c>SameSite=Lax</c> cookies travel" — the reason the closing page navigates rather
/// than firing a beacon — was asserted in comments and executed by nothing. If it is wrong the join
/// silently returns no identity: no error, no red test, just permanently unjoined events.</item>
/// <item>Nothing ran the closing page's <c>location.replace</c>. The other test regex-extracts the
/// URL out of the HTML and follows it by hand, so the emitted script was never proven to navigate.
/// </item>
/// </list>
///
/// <para><b>Gated</b> behind <c>KCAP_JOIN_BROWSER_E2E=&lt;base url&gt;</c> plus
/// <c>KCAP_JOIN_BROWSER_HANDSHAKE=&lt;path&gt;</c>. It cannot drive the browser itself — a browser
/// is not something a unit-test host should own — so it publishes the callback URL to the handshake
/// path and waits. Whoever runs it (an agent with browser automation, or a human with the URL in
/// their clipboard) navigates to that URL, and the assertions below check what came back.</para>
///
/// <para>127.0.0.1 and localhost are DIFFERENT SITES to a browser, so the hop out of the closing
/// page is a genuine cross-site top-level navigation — exactly the case <c>SameSite=Lax</c> governs,
/// and exactly what needed proving.</para>
///
/// <para><b>Fires no analytics event.</b> The web route captures <c>cli_auth_return</c> only when the
/// analytics cookie carries a <c>distinct_id</c>; the cookie the driver sets has <c>$device_id</c>
/// ONLY. The device id is what crosses back, so the merge is still fully exercised.</para>
/// </summary>
[NotInParallel]
public class JoinChainBrowserTests : IDisposable {
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    const string GateEnvVar      = "KCAP_JOIN_BROWSER_E2E";
    const string HandshakeEnvVar = "KCAP_JOIN_BROWSER_HANDSHAKE";

    /// <summary>Stands in for the per-login CSRF state OidcClient puts in the authorize URL. The
    /// closing page's redirect is gated on the callback echoing it, so the driver must send it back.
    /// </summary>
    const string State = "browser-e2e-state";

    /// <summary>Generous: a human or an agent has to drive a browser between the handshake being
    /// written and the return hop landing.</summary>
    static readonly TimeSpan DriverBudget = TimeSpan.FromMinutes(3);

    [Test]
    public async Task A_real_browser_carries_the_key_out_and_the_web_identity_back() {
        var baseUrl   = Environment.GetEnvironmentVariable(GateEnvVar);
        var handshake = Environment.GetEnvironmentVariable(HandshakeEnvVar);

        Skip.Unless(!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(handshake),
            $"Set {GateEnvVar}=<base url> and {HandshakeEnvVar}=<path> to run the browser chain.");

        var priorSignup = Environment.GetEnvironmentVariable("KCAP_SIGNUP_URL");

        try {
            Environment.SetEnvironmentVariable("KCAP_SIGNUP_URL", baseUrl);
            CliTelemetry.Reset();
            SetupJoin.Reset();

            TelemetryState.PathOverride    = _tmp.PathTo("telemetry.json");
            TelemetryDeviceId.PathOverride = _tmp.PathTo("telemetry-device.json");
            var sink = new List<TelemetryEvent>();
            CliTelemetry.TestSink = sink;
            CliTelemetry.Initialize("setup", null, loggedIn: false);
            TelemetryTestGuards.AssertEnabled("setup");

            var key = SetupJoin.Mint();
            await Assert.That(key).IsNotNull();

            var port     = OAuthLoginFlow.GetAvailablePort();
            var redirect = $"http://127.0.0.1:{port}/callback";

            using var browser = new LoopbackBrowser(openBrowser: _ => true, join: SetupJoin.Loopback) {
                DrainCap = DriverBudget, DisposeWait = TimeSpan.FromSeconds(10),
            };

            var invoke = browser.InvokeAsync(
                new BrowserOptions($"http://example.test/authorize?state={State}", redirect));

            // Published only after InvokeAsync has bound the listener, so a driver that navigates the
            // instant it sees this file cannot beat the port into existence.
            PublishHandshake(handshake!, baseUrl!, port, $"{redirect}?code=browser-e2e&state={State}");

            var result = await invoke;
            await Assert.That(result.ResultType).IsEqualTo(BrowserResultType.Success)
                .Because("the browser's callback must complete sign-in, not fail it");

            // The hops and the return to /joined happen in the browser after the closing page is
            // served, so the merge lands asynchronously. Poll the shared properties for it.
            var merged = await WaitForMerge(sink, DriverBudget);

            await Assert.That(merged).IsNotNull()
                .Because("a real browser must carry the web identity back to /joined");
            await Assert.That(merged!["join_id"]!.GetValue<string>()).IsEqualTo(key);
            await Assert.That(merged["web_device_id_capacitor"]!.GetValue<string>()).IsNotEmpty()
                .Because("SameSite=Lax cookies must travel on this top-level navigation — the whole "
                       + "reason the closing page navigates instead of firing a beacon");
        } finally {
            Environment.SetEnvironmentVariable("KCAP_SIGNUP_URL", priorSignup);
            try { File.Delete(handshake!); } catch { /* best effort */ }
            CliTelemetry.Reset();
            SetupJoin.Reset();
        }
    }

    /// <summary>Everything the driver needs, written atomically so a poller never reads half a file.
    /// </summary>
    static void PublishHandshake(string path, string baseUrl, int port, string callbackUrl) {
        var payload = new JsonObject {
            ["callbackUrl"] = callbackUrl,
            ["joinedUrl"]   = $"http://127.0.0.1:{port}/joined",
            ["baseUrl"]     = baseUrl,
            ["port"]        = port,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var staging = path + ".tmp";
        File.WriteAllText(staging, payload);
        File.Move(staging, path, overwrite: true);
    }

    /// <summary>
    /// The shared properties once the return hop has been accepted, or null if it never arrived.
    /// Probing with a real capture is how the other suites read shared state, and it is what a real
    /// event would carry.
    /// </summary>
    static async Task<JsonObject?> WaitForMerge(List<TelemetryEvent> sink, TimeSpan budget) {
        var deadline = DateTimeOffset.UtcNow + budget;

        while (DateTimeOffset.UtcNow < deadline) {
            sink.Clear();
            CliTelemetry.Capture("cli_browser_e2e_probe", new JsonObject());

            if (sink.Count > 0 && sink[^1].Properties["web_device_id_capacitor"] is not null)
                return sink[^1].Properties;

            await Task.Delay(500);
        }

        return null;
    }
}
