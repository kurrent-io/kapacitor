using Capacitor.Cli.Core.Auth;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Tests.Unit;

// These tests bind real loopback HttpListeners on OS-assigned ports. Run fully exclusively
// (no other test running) so the rest of the parallel suite can't grab the freed ephemeral
// port in the alloc->bind window — the managed HttpListener on macOS/Linux throws
// "Address already in use". (Production is single-use interactive, so the race is irrelevant there.)
[NotInParallel]
public class LoopbackBrowserTests {
    [Test]
    public async Task Returns_success_with_raw_query_on_callback() {
        var port     = OAuthLoginFlow.GetAvailablePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        var browser  = new LoopbackBrowser(openBrowser: _ => { }); // don't launch a real browser

        var invoke = browser.InvokeAsync(new BrowserOptions("http://example.test/authorize", redirect));

        using var http = new HttpClient();
        // Listener is started synchronously before InvokeAsync's first await, so this connects.
        _ = await http.GetAsync($"{redirect}?code=abc&state=xyz");

        var result = await invoke;
        await Assert.That(result.ResultType).IsEqualTo(BrowserResultType.Success);
        await Assert.That(result.Response).Contains("code=abc");
        await Assert.That(result.Response).Contains("state=xyz");
    }

    [Test]
    public async Task Returns_timeout_when_no_callback_arrives() {
        var port     = OAuthLoginFlow.GetAvailablePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        var browser  = new LoopbackBrowser(openBrowser: _ => { });

        var result = await browser.InvokeAsync(
            new BrowserOptions("http://example.test/authorize", redirect) { Timeout = TimeSpan.FromMilliseconds(200) });

        await Assert.That(result.ResultType).IsEqualTo(BrowserResultType.Timeout);
    }

    // A caller who closed the wizard did not time out: the cancel propagates so the flow answers
    // Cancelled, instead of being rendered as "Timed out waiting for authorization".
    [Test]
    public async Task Caller_cancellation_propagates_instead_of_reporting_a_timeout() {
        var port     = OAuthLoginFlow.GetAvailablePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        var browser  = new LoopbackBrowser(openBrowser: _ => { });

        using var cts = new CancellationTokenSource();

        var invoke = browser.InvokeAsync(
            new BrowserOptions("http://example.test/authorize", redirect) { Timeout = TimeSpan.FromMinutes(5) }, cts.Token);

        await cts.CancelAsync();

        await Assert.That(async () => await invoke).Throws<OperationCanceledException>();
    }
}
