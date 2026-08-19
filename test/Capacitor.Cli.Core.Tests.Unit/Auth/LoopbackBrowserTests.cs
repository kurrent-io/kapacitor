using System.Net;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

// These tests bind real loopback HttpListeners on OS-assigned ports. Run fully exclusively
// (no other test running) so the rest of the parallel suite can't grab the freed ephemeral
// port in the alloc->bind window — the managed HttpListener on macOS/Linux throws
// "Address already in use". (Production is single-use interactive, so the race is irrelevant there.)
[NotInParallel]
public class LoopbackBrowserTests {
    /// <summary>Records what the browser asked for and what came back, touching no statics.</summary>
    sealed class RecordingJoin(string? firstHop) : ILoopbackJoin {
        public int     PortAsked { get; private set; }
        public string? Accepted  { get; private set; }

        public string? FirstHopUrl(int port) {
            PortAsked = port;

            return firstHop;
        }

        public bool Accept(string query) { Accepted = query; return true; }
    }

    /// <summary>A join whose FirstHopUrl throws, to prove the browser never depends on it.</summary>
    sealed class ThrowingJoin : ILoopbackJoin {
        public string? FirstHopUrl(int port) => throw new InvalidOperationException("boom");
        public bool Accept(string query)     => throw new InvalidOperationException("boom");
    }

    static bool IsPortFree(int port) {
        try {
            var probe = new HttpListener();
            probe.Prefixes.Add($"http://127.0.0.1:{port}/");
            probe.Start();
            probe.Close();

            return true;
        } catch {
            return false;
        }
    }

    // The listener is torn down asynchronously on some paths, so poll rather than assert once.
    static async Task<bool> EventuallyFree(int port) {
        for (var i = 0; i < 100; i++) {
            if (IsPortFree(port)) return true;
            await Task.Delay(50);
        }

        return false;
    }

    static BrowserOptions Options(string redirect, TimeSpan? timeout = null) =>
        timeout is null
            ? new BrowserOptions("http://example.test/authorize", redirect)
            : new BrowserOptions("http://example.test/authorize", redirect) { Timeout = timeout.Value };

    static (int Port, string Redirect) Loopback() {
        var port = OAuthLoginFlow.GetAvailablePort();

        return (port, $"http://127.0.0.1:{port}/callback");
    }

    [Test]
    public async Task Returns_success_with_raw_query_on_callback() {
        var (port, redirect) = Loopback();
        using var browser = new LoopbackBrowser(openBrowser: _ => true); // don't launch a real browser

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        // The listener starts synchronously before InvokeAsync's first await, so this connects.
        _ = await http.GetAsync($"{redirect}?code=abc&state=xyz");

        var result = await invoke;
        await Assert.That(result.ResultType).IsEqualTo(BrowserResultType.Success);
        await Assert.That(result.Response).Contains("code=abc");
        await Assert.That(result.Response).Contains("state=xyz");
        await Assert.That(port).IsGreaterThan(0);
    }

    [Test]
    public async Task Returns_timeout_when_no_callback_arrives() {
        var (port, redirect) = Loopback();
        using var browser = new LoopbackBrowser(openBrowser: _ => true);

        var result = await browser.InvokeAsync(Options(redirect, TimeSpan.FromMilliseconds(200)));

        await Assert.That(result.ResultType).IsEqualTo(BrowserResultType.Timeout);
        await Assert.That(await EventuallyFree(port)).IsTrue();
    }

    // On /callback these are a security control, not hygiene: that URL's query holds the OAuth
    // code, and a browser configured with unsafe-url would otherwise put it in a Referer header
    // on the first cross-origin hop — into our own access logs.
    [Test]
    public async Task Callback_response_carries_the_hygiene_headers() {
        var (_, redirect) = Loopback();
        using var browser = new LoopbackBrowser(openBrowser: _ => true);

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        var resp = await http.GetAsync($"{redirect}?code=abc&state=xyz");
        await invoke;

        await Assert.That(resp.Headers.GetValues("Referrer-Policy").First()).IsEqualTo("no-referrer");
        await Assert.That(resp.Headers.CacheControl!.NoStore).IsTrue();
    }

    [Test]
    public async Task Success_emits_the_redirect_and_the_success_text() {
        var (port, redirect) = Loopback();
        var join = new RecordingJoin($"https://example.test/api/cli/return?j=deadbeef&p={port}");
        using var browser = new LoopbackBrowser(openBrowser: _ => true, join: join) {
            DrainCap = TimeSpan.FromMilliseconds(400), DisposeWait = TimeSpan.FromMilliseconds(400),
        };

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        var body = await (await http.GetAsync($"{redirect}?code=abc&state=xyz")).Content.ReadAsStringAsync();
        await invoke;

        await Assert.That(join.PortAsked).IsEqualTo(port);
        await Assert.That(body).Contains("Authentication successful!");
        await Assert.That(body).Contains("location.replace(");
        await Assert.That(body).Contains("j=deadbeef");
    }

    [Test]
    public async Task Joined_arrival_reaches_Accept_and_frees_the_port() {
        var (port, redirect) = Loopback();
        var join = new RecordingJoin("https://example.test/api/cli/return?j=k&p=1");
        var browser = new LoopbackBrowser(openBrowser: _ => true, join: join) {
            DrainCap = TimeSpan.FromSeconds(5), DisposeWait = TimeSpan.FromSeconds(5),
        };

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        _ = await http.GetAsync($"{redirect}?code=abc&state=xyz");
        await invoke;

        var joined = await http.GetAsync($"http://127.0.0.1:{port}/joined?j=k&v=legacy&w1=cap&w2=www");
        var body   = await joined.Content.ReadAsStringAsync();

        await Assert.That(join.Accepted).IsNotNull();
        await Assert.That(join.Accepted!).Contains("w1=cap");
        await Assert.That(body).Contains("history.replaceState(");
        await Assert.That(joined.Headers.GetValues("Referrer-Policy").First()).IsEqualTo("no-referrer");

        browser.Dispose();
        await Assert.That(await EventuallyFree(port)).IsTrue();
    }

    [Test]
    public async Task Drain_gives_up_at_its_cap_when_joined_never_arrives() {
        var (port, redirect) = Loopback();
        var join = new RecordingJoin("https://example.test/api/cli/return?j=k&p=1");
        using var browser = new LoopbackBrowser(openBrowser: _ => true, join: join) {
            DrainCap = TimeSpan.FromMilliseconds(300), DisposeWait = TimeSpan.FromMilliseconds(300),
        };

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        _ = await http.GetAsync($"{redirect}?code=abc&state=xyz");
        await invoke;

        await Assert.That(await EventuallyFree(port)).IsTrue();
        await Assert.That(join.Accepted).IsNull();
    }

    // For the short-lived `kcap login` the caller finishes seconds after the browser returns, so
    // Dispose must reclaim the port on its own bound rather than waiting out the drain cap.
    [Test]
    public async Task Dispose_reclaims_the_listener_at_its_wait_when_the_drain_is_still_pending() {
        var (port, redirect) = Loopback();
        var join = new RecordingJoin("https://example.test/api/cli/return?j=k&p=1");
        var browser = new LoopbackBrowser(openBrowser: _ => true, join: join) {
            DrainCap = TimeSpan.FromSeconds(30), DisposeWait = TimeSpan.FromMilliseconds(200),
        };

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        _ = await http.GetAsync($"{redirect}?code=abc&state=xyz");
        await invoke;

        browser.Dispose(); // must not wait out the 30s cap
        await Assert.That(await EventuallyFree(port)).IsTrue();
    }

    [Test]
    public async Task An_auth_error_callback_emits_no_redirect_and_arms_no_drain() {
        var (port, redirect) = Loopback();
        var join = new RecordingJoin("https://example.test/api/cli/return?j=k&p=1");
        using var browser = new LoopbackBrowser(openBrowser: _ => true, join: join) {
            DrainCap = TimeSpan.FromSeconds(30), DisposeWait = TimeSpan.FromMilliseconds(200),
        };

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        var body = await (await http.GetAsync($"{redirect}?error=access_denied")).Content.ReadAsStringAsync();
        await invoke;

        await Assert.That(body).Contains("Authentication failed");
        await Assert.That(body).DoesNotContain("location.replace(");
        // Disposed inline: no drain is holding the port, despite the 30s cap.
        await Assert.That(await EventuallyFree(port)).IsTrue();
    }

    [Test]
    public async Task An_openBrowser_that_throws_propagates_and_still_frees_the_port() {
        var (port, redirect) = Loopback();
        using var browser = new LoopbackBrowser(openBrowser: _ => throw new InvalidOperationException("boom"));

        await Assert.That(async () => await browser.InvokeAsync(Options(redirect)))
            .Throws<InvalidOperationException>();

        await Assert.That(await EventuallyFree(port)).IsTrue();
    }

    // Telemetry must never break auth. A join that throws costs the redirect, nothing else.
    [Test]
    public async Task A_join_that_throws_still_completes_the_callback() {
        var (port, redirect) = Loopback();
        using var browser = new LoopbackBrowser(openBrowser: _ => true, join: new ThrowingJoin());

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        var body = await (await http.GetAsync($"{redirect}?code=abc&state=xyz")).Content.ReadAsStringAsync();

        var result = await invoke;
        await Assert.That(result.ResultType).IsEqualTo(BrowserResultType.Success);
        await Assert.That(body).DoesNotContain("location.replace(");
        await Assert.That(await EventuallyFree(port)).IsTrue();
    }

    [Test]
    public async Task A_non_joined_request_during_the_drain_gets_404_and_the_drain_keeps_waiting() {
        var (port, redirect) = Loopback();
        var join = new RecordingJoin("https://example.test/api/cli/return?j=k&p=1");
        using var browser = new LoopbackBrowser(openBrowser: _ => true, join: join) {
            DrainCap = TimeSpan.FromSeconds(10), DisposeWait = TimeSpan.FromSeconds(5),
        };

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        _ = await http.GetAsync($"{redirect}?code=abc&state=xyz");
        await invoke;

        var favicon = await http.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
        await Assert.That(favicon.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        var joined = await http.GetAsync($"http://127.0.0.1:{port}/joined?j=k&w1=cap");
        await Assert.That(joined.IsSuccessStatusCode).IsTrue();
        await Assert.That(join.Accepted).IsNotNull();
    }

    // Without a join, behaviour must be what it was before any of this existed.
    [Test]
    public async Task Without_a_join_the_response_body_is_byte_identical_to_today() {
        var (port, redirect) = Loopback();
        using var browser = new LoopbackBrowser(openBrowser: _ => true);

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        var body = await (await http.GetAsync($"{redirect}?code=abc&state=xyz")).Content.ReadAsStringAsync();
        await invoke;

        await Assert.That(body).IsEqualTo(
            "<html><body style='font-family:system-ui;max-width:480px;margin:80px auto;text-align:center'>"
          + "<h2>Authentication successful!</h2><p>You can close this window and return to the terminal.</p></body></html>");
        await Assert.That(await EventuallyFree(port)).IsTrue();
    }

    [Test]
    public async Task Dispose_twice_is_a_no_op() {
        var (port, redirect) = Loopback();
        var browser = new LoopbackBrowser(openBrowser: _ => true);

        _ = await browser.InvokeAsync(Options(redirect, TimeSpan.FromMilliseconds(100)));

        browser.Dispose();
        browser.Dispose(); // must not throw

        await Assert.That(await EventuallyFree(port)).IsTrue();
    }

    // Disposing without ever invoking must be safe: a bind failure means the two callers that
    // construct-then-dispose never got as far as a listener.
    [Test]
    public async Task Dispose_without_invoking_is_a_no_op() {
        var browser = new LoopbackBrowser(openBrowser: _ => true);

        await Assert.That(browser.Dispose).ThrowsNothing();
    }

    // The one property the rest of this file carries by argument rather than by evidence: every
    // other test drives a single winner and checks the port ends free. Here the drain's own
    // teardown and Dispose are deliberately aimed at the same instant from different threads,
    // repeatedly. Shutting down twice throws; shutting down never holds the port. Neither may
    // happen even once.
    [Test]
    public async Task Racing_the_drain_against_Dispose_tears_down_exactly_once() {
        for (var attempt = 0; attempt < 25; attempt++) {
            var (port, redirect) = Loopback();
            var join = new RecordingJoin("https://example.test/api/cli/return?j=k&p=1");
            var browser = new LoopbackBrowser(openBrowser: _ => true, join: join) {
                // A cap short enough that the drain is giving up at roughly the moment Dispose
                // arrives, so the two teardown paths overlap instead of queueing.
                DrainCap = TimeSpan.FromMilliseconds(60), DisposeWait = TimeSpan.FromMilliseconds(60),
            };

            var invoke = browser.InvokeAsync(Options(redirect));

            using var http = new HttpClient();
            _ = await http.GetAsync($"{redirect}?code=abc&state=xyz");
            await invoke;

            // Both parties enter teardown concurrently; an unguarded second Stop/Close would
            // surface here as a faulted task or an ObjectDisposedException.
            var racer = Task.Run(browser.Dispose);
            browser.Dispose();
            await racer;

            await Assert.That(await EventuallyFree(port)).IsTrue();
        }
    }

    /// <summary>Accepts only the one key it was given, like the real join does.</summary>
    sealed class KeyedJoin(string? firstHop, string key) : ILoopbackJoin {
        public int Attempts { get; private set; }
        public string? Accepted { get; private set; }

        public string? FirstHopUrl(int port) => firstHop;

        public bool Accept(string query) {
            Attempts++;
            if (!query.Contains($"j={key}", StringComparison.Ordinal)) return false;
            Accepted = query;

            return true;
        }
    }

    // A junk return hop must not end the wait. SetupJoin.Accept already refuses to consume the
    // one-shot on a key mismatch, but that protection is worthless if the listener dies anyway:
    // any local process could fire one request at the port and the real browser would then arrive
    // at a closed socket, losing the merge. So a rejected /joined is answered and the wait
    // continues, exactly like any other stray request.
    [Test]
    public async Task A_joined_request_with_the_wrong_key_does_not_end_the_wait() {
        var (port, redirect) = Loopback();
        var join = new KeyedJoin("https://example.test/api/cli/return?j=real&p=1", "real");
        using var browser = new LoopbackBrowser(openBrowser: _ => true, join: join) {
            DrainCap = TimeSpan.FromSeconds(10), DisposeWait = TimeSpan.FromSeconds(5),
        };

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        _ = await http.GetAsync($"{redirect}?code=abc&state=xyz");
        await invoke;

        // An impostor gets in first.
        var junk = await http.GetAsync($"http://127.0.0.1:{port}/joined?j=junk&w1=attacker");
        await Assert.That(junk.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(join.Accepted).IsNull();

        // The real browser still lands, and still merges.
        var real = await http.GetAsync($"http://127.0.0.1:{port}/joined?j=real&w1=cap");
        await Assert.That(real.IsSuccessStatusCode).IsTrue();
        await Assert.That(join.Accepted).IsNotNull();
        await Assert.That(join.Accepted!).Contains("w1=cap");
        await Assert.That(join.Attempts).IsEqualTo(2);
    }

    // Repeated junk cannot hold the port open forever either — the cap still governs.
    [Test]
    public async Task Repeated_junk_return_hops_are_still_bounded_by_the_cap() {
        var (port, redirect) = Loopback();
        var join = new KeyedJoin("https://example.test/api/cli/return?j=real&p=1", "real");
        using var browser = new LoopbackBrowser(openBrowser: _ => true, join: join) {
            DrainCap = TimeSpan.FromMilliseconds(600), DisposeWait = TimeSpan.FromMilliseconds(600),
        };

        var invoke = browser.InvokeAsync(Options(redirect));

        using var http = new HttpClient();
        _ = await http.GetAsync($"{redirect}?code=abc&state=xyz");
        await invoke;

        for (var i = 0; i < 3; i++) {
            try { _ = await http.GetAsync($"http://127.0.0.1:{port}/joined?j=junk{i}"); } catch { }
        }

        await Assert.That(await EventuallyFree(port)).IsTrue();
        await Assert.That(join.Accepted).IsNull();
    }

    // Two properties at once, and the reason the drain is armed only after the closing page is
    // fully written.
    //
    // First: sign-in must not fail. The browser can start its round trip the moment it holds the
    // body, before this side has finished closing the response — so a drain running concurrently
    // could accept the return hop and close the shared listener out from under that response,
    // surfacing as a throw out of InvokeAsync. Telemetry breaking authentication.
    //
    // Second: nothing is lost by arming late. A return hop that lands in the gap is queued by the
    // listener and collected when the drain starts. If that were false this test would fail, which
    // is the point — the fix depends on it.
    //
    // Repeated, because the window is timing-dependent and a single pass proves little.
    [Test]
    public async Task A_return_hop_arriving_before_sign_in_returns_is_still_accepted() {
        for (var attempt = 0; attempt < 15; attempt++) {
            var (port, redirect) = Loopback();
            var join = new KeyedJoin("https://example.test/api/cli/return?j=real&p=1", "real");
            using var browser = new LoopbackBrowser(openBrowser: _ => true, join: join) {
                DrainCap = TimeSpan.FromSeconds(10), DisposeWait = TimeSpan.FromSeconds(5),
            };

            var invoke = browser.InvokeAsync(Options(redirect));

            using var http = new HttpClient();
            _ = await http.GetAsync($"{redirect}?code=abc&state=xyz");

            // Deliberately NOT awaiting invoke first: this is the hop landing in the window.
            var joined = await http.GetAsync($"http://127.0.0.1:{port}/joined?j=real&w1=cap");

            var result = await invoke;

            await Assert.That(result.ResultType).IsEqualTo(BrowserResultType.Success)
                .Because("a return hop must never be able to fail sign-in");
            await Assert.That(joined.IsSuccessStatusCode).IsTrue();
            await Assert.That(join.Accepted).IsNotNull().Because("an early hop is queued, not lost");
            await Assert.That(join.Accepted!).Contains("w1=cap");
        }
    }

    // A caller who closed the wizard did not time out: the cancel propagates so the flow answers
    // Cancelled, instead of being rendered as "Timed out waiting for authorization".
    [Test]
    public async Task Caller_cancellation_propagates_instead_of_reporting_a_timeout() {
        var (_, redirect) = Loopback();
        using var browser = new LoopbackBrowser(openBrowser: _ => true);

        using var cts = new CancellationTokenSource();

        var invoke = browser.InvokeAsync(Options(redirect, TimeSpan.FromMinutes(5)), cts.Token);

        await cts.CancelAsync();

        await Assert.That(async () => await invoke).Throws<OperationCanceledException>();
    }
}
