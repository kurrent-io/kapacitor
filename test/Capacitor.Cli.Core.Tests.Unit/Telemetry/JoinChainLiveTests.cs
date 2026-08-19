using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>
/// The whole chain, driven end to end against a REAL kcap-web instance: mint, the real
/// <see cref="LoopbackBrowser"/> and its real listener, the real closing page, the real
/// <c>/api/cli/return</c> route over HTTP, both hops, the real return to <c>/joined</c>, and the
/// real <see cref="SetupJoin.Accept"/> merge.
///
/// <para><b>Gated</b> behind <c>KCAP_JOIN_E2E=&lt;base url&gt;</c> — e.g.
/// <c>KCAP_JOIN_E2E=http://localhost:4321</c> with kcap-web's dev server running. CI has no
/// kcap-web, and the route is reachable only from a CLI redirect, so an ungated run costs a skip.</para>
///
/// <para>This exists because every OTHER test on this feature is a unit test asserting behaviour the
/// same author specified. The one real defect found on the web half — a malformed cookie turning
/// sign-in into a 500 — was found by a reviewer, not by those tests. This is the case that would
/// have caught a mismatch between the two halves: a wrong query-parameter name, a wrong host rule,
/// a redirect that never comes back.</para>
///
/// <para><b>Deliberately fires no analytics event.</b> The web route captures
/// <c>cli_auth_return</c> only when the analytics cookie carries a <c>distinct_id</c>, so the cookie
/// below has <c>$device_id</c> ONLY. That still exercises the full merge — the device id is what
/// crosses back — while writing nothing into the real PostHog project.</para>
/// </summary>
// Bare [NotInParallel], not the telemetry PathOverride keys: this captures Console, which is
// process-global, and ConsoleOutput rejects an overlapping capture. Ungrouped is strictly stronger.
[NotInParallel]
public class JoinChainLiveTests : IDisposable {
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    const string GateEnvVar = "KCAP_JOIN_E2E";

    // Matches PostHog's own cookie name, built the same way the route builds it.
    const string PhCookie = "ph_phc_DeHBgHGersY4LmDlADnPrsCPOAmMO7QFOH8f4DVEVmD_posthog";

    const string CapDeviceId = "e2e-cap-device";

    [Test]
    public async Task The_whole_chain_carries_the_key_out_and_the_web_identity_back() {
        var baseUrl = Environment.GetEnvironmentVariable(GateEnvVar);
        Skip.Unless(!string.IsNullOrWhiteSpace(baseUrl),
            $"Set {GateEnvVar}=<base url> with kcap-web running (e.g. http://localhost:4321) to run the live chain.");

        using var console = ConsoleOutput.StartErrorCapture();

        var priorSignup = Environment.GetEnvironmentVariable("KCAP_SIGNUP_URL");
        var priorDebug  = Environment.GetEnvironmentVariable("KCAP_TELEMETRY_DEBUG");

        try {
            // FirstHopUrl reads this, which is the whole reason the override exists.
            Environment.SetEnvironmentVariable("KCAP_SIGNUP_URL", baseUrl);
            Environment.SetEnvironmentVariable("KCAP_TELEMETRY_DEBUG", "1");

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

            using var browser = new LoopbackBrowser(openBrowser: _ => { }, join: SetupJoin.Loopback) {
                DrainCap = TimeSpan.FromSeconds(30), DisposeWait = TimeSpan.FromSeconds(10),
            };

            var invoke = browser.InvokeAsync(new BrowserOptions("http://example.test/authorize", redirect));

            // Stand in for the browser: carry the site's cookies, and follow redirects the way a
            // navigation would. $device_id only — see the class doc on firing no event.
            var jar = new CookieContainer();
            var web = new Uri(baseUrl!);
            jar.Add(web, new Cookie("kcap_consent", "accepted"));
            jar.Add(web, new Cookie("kcap_ab", "redesign"));
            jar.Add(web, new Cookie(PhCookie,
                Uri.EscapeDataString(JsonSerializer.Serialize(new JsonObject { ["$device_id"] = CapDeviceId }))));

            using var handler = new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true };
            using var http    = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            // 1. The callback lands on the CLI's own listener and gets today's page plus a redirect.
            var closing = await http.GetStringAsync($"{redirect}?code=e2e-fake-code&state=e2e-fake-state");
            await Assert.That(closing).Contains("Authentication successful!");

            var emitted = Regex.Match(closing, @"location\.replace\(""([^""]+)""\)");
            await Assert.That(emitted.Success).IsTrue().Because("the closing page must carry the first hop");

            // JsonEncodedText escapes & as & inside the JS string literal.
            var firstHop = Regex.Unescape(emitted.Groups[1].Value);
            await Assert.That(firstHop).StartsWith($"{baseUrl}/api/cli/return");
            await Assert.That(firstHop).Contains($"j={key}");
            await Assert.That(firstHop).Contains($"p={port}");

            // 2. Follow it the way a browser would: hop 1 -> hop 2 -> back to /joined on our listener.
            var landed = await http.GetAsync(firstHop);
            await Assert.That(landed.IsSuccessStatusCode).IsTrue();
            await Assert.That(landed.RequestMessage!.RequestUri!.ToString()).Contains("/joined");
            await Assert.That(await landed.Content.ReadAsStringAsync()).Contains("history.replaceState(");

            var result = await invoke;
            await Assert.That(result.ResultType).IsEqualTo(BrowserResultType.Success);

            // 3. The merge actually happened, on the real return trip.
            sink.Clear();
            CliTelemetry.Capture("cli_e2e_probe", new JsonObject());
            var props = sink[^1].Properties;

            await Assert.That(props["join_id"]!.GetValue<string>()).IsEqualTo(key);
            await Assert.That(props["web_device_id_capacitor"]!.GetValue<string>()).IsEqualTo(CapDeviceId);
            await Assert.That(props["site_variant"]!.GetValue<string>()).IsEqualTo("redesign");

            // 4. Debug shows the key is ATTACHED without ever showing the key.
            var printed = console.GetCapturedError();
            await Assert.That(printed).Contains("\"join_id\":\"[set]\"");
            await Assert.That(printed).DoesNotContain(key!);
        } finally {
            Environment.SetEnvironmentVariable("KCAP_SIGNUP_URL", priorSignup);
            Environment.SetEnvironmentVariable("KCAP_TELEMETRY_DEBUG", priorDebug);
            CliTelemetry.Reset();
            SetupJoin.Reset();
        }
    }
}
