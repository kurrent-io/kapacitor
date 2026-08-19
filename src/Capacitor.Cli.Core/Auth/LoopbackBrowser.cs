using System.Net;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Telemetry;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// OidcClient <see cref="IBrowser"/> backed by a 127.0.0.1 loopback HttpListener.
/// Opens the system browser to the authorize URL, waits for the redirect callback,
/// and returns its raw query string. WorkOS documents the loopback exception as
/// 127.0.0.1 (not localhost). The bind exception is intentionally NOT caught so the
/// GitHub flow can fall back to device flow on a bind failure. A caller cancel throws
/// <see cref="OperationCanceledException"/>; only the independent timeout returns Timeout.
///
/// <para>With an <see cref="ILoopbackJoin"/> the closing page additionally navigates the browser
/// out through kcap-web and back to <c>/joined</c> on THIS SAME listener — which means the listener
/// must OUTLIVE <see cref="InvokeAsync"/>. That removes the <c>using var</c> safety net this class
/// used to rely on, so ownership becomes explicit: exactly one owner stops and disposes, exactly
/// once, decided by an <see cref="Interlocked"/> flip of <see cref="_tornDown"/>. Handoff happens at
/// exactly ONE point — after a successful callback whose closing-page redirect was written cleanly.
/// Every other exit (timeout, caller cancel, an auth-error callback, an injected <c>openBrowser</c>
/// that throws, a closing-page write that throws) keeps ownership and disposes, exactly as before.</para>
///
/// <para>The redirect needs TWO things: the callback carried no <c>error=</c>, and it echoed the
/// authorize URL's <c>state</c>. The second is a security control, not a success signal — this port
/// is reachable by any local process, and without it a bare <c>/callback?code=junk</c> would be
/// answered with a page containing the join key (see <see cref="StateEchoed"/>). It is still NOT an
/// auth-success signal: the real CSRF validation and the token exchange both happen after this
/// method returns, so a run that ends up failing authentication can still have emitted the redirect.
/// Nothing here is on the critical path; the token exchange neither waits for the hops nor is
/// affected by them.</para>
/// </summary>
/// <param name="hint">
/// An escape hatch offered while the wait runs, printed under the "visit:" line rather than before it:
/// above, it reads as an alternative to signing in at all instead of an alternative to that URL.
/// </param>
public sealed class LoopbackBrowser(
        Func<string, bool>? openBrowser = null,
        IAuthProgress?      progress    = null,
        string?             hint        = null,
        ILoopbackJoin?      join        = null) : IBrowser, IDisposable {
    readonly Func<string, bool> _openBrowser = openBrowser ?? SystemBrowser.TryOpen;
    readonly IAuthProgress  _progress    = progress ?? ConsoleAuthProgress.Instance;
    readonly ILoopbackJoin? _join        = join;

    HttpListener? _listener;
    Task?         _drain;
    int           _tornDown;

    /// <summary>How long the background drain waits for the browser's return hop. Test seam.</summary>
    internal TimeSpan DrainCap { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long <see cref="Dispose"/> lets a pending drain finish before reclaiming the listener.
    /// Bounded so the short-lived <c>kcap login</c> cannot hang, but non-zero so its browser's last
    /// hop finds a live port instead of a connection error. Test seam.
    /// </summary>
    internal TimeSpan DisposeWait { get; init; } = TimeSpan.FromSeconds(3);

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken ct = default) {
        var port = new Uri(options.EndUrl).Port;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start(); // bind failure propagates (HttpListenerException / PlatformNotSupportedException)
        _listener = listener;

        var handedOff = false;

        try {
            // Bind, launch, THEN announce: nothing is said until there is something true to say, or a
            // failed launch has already printed the browser narrative and a 300-character authorize URL
            // for a route the reader cannot take. (The listener still binds first, so a fast browser
            // cannot beat it.)
            //
            // Thrown rather than waited out: with no browser here, the callback can only be reached from a
            // browser on this machine, and there isn't one. Five minutes of listening ends in the same
            // place, having offered a URL that leads to a connection refused.
            //
            // INSIDE the try, unlike the version this merged with: the listener is already bound, so a
            // throw that escapes before the try would keep the port for the life of the process. Harmless
            // in a CLI that is about to exit, a real leak in the desktop app, which outlives the flow.
            if (!_openBrowser(options.StartUrl)) throw new BrowserLaunchException();

            _progress.BrowserOpening(options.StartUrl);
            if (hint is not null) _progress.Notice(hint);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(options.Timeout);

            HttpListenerContext context;

            while (true) {
                var getContext = listener.GetContextAsync();

                try {
                    context = await getContext.WaitAsync(cts.Token);
                } catch (OperationCanceledException) {
                    listener.Stop();
                    Observe(getContext);

                    // The caller's own cancel is not a timeout — it propagates so the flow answers Cancelled.
                    ct.ThrowIfCancellationRequested();

                    return new BrowserResult { ResultType = BrowserResultType.Timeout };
                }

                if (context.Request.Url?.AbsolutePath == "/callback") break;

                // Ignore favicon and other browser-issued requests that aren't our callback.
                context.Response.StatusCode = 404;
                context.Response.Close();
            }

            var query   = context.Request.Url?.Query ?? "";
            var success = !query.Contains("error=");

            // Gated on the callback echoing the authorize URL's CSRF state, because this port is
            // reachable by ANY local process and the closing page would otherwise HAND OUT the join
            // key: a bare `/callback?code=junk` used to be answered with the first-hop URL, key
            // included. A local process could read it from the HTML and spend it on
            // `/joined?j=<stolen>&w1=<its own id>` before the real browser arrived, consuming the
            // one-shot and merging values it chose. The downstream CSRF check cannot undo either —
            // it only makes authentication fail, after the disclosure.
            //
            // Ending the wait is deliberately NOT gated. That behaviour, and the local
            // denial-of-service it allows, predate this feature; narrowing it here would change the
            // auth path, which this feature must not do.
            var firstHop = success && StateEchoed(options, query) ? SafeFirstHop(port) : null;

            await WritePageAsync(context, success, redirectTo: firstHop, joined: false);

            // Armed only AFTER this response is fully written, and that ordering is load-bearing.
            // The browser can begin its round trip the moment it has the body — before Close() runs
            // on this side — so a drain started any earlier could accept the return hop and close
            // the shared listener while this very response was still completing. That abort
            // surfaces here as a throw, i.e. telemetry failing authentication, which is the one
            // thing it must never do.
            //
            // A return hop that arrives in the gap is NOT lost: a listener queues requests that
            // arrive with no accept pending, so the drain collects it as soon as it starts.
            //
            // Handoff only after a clean write. A write that throws leaves ownership here and the
            // finally below tears down, with no drain to race.
            handedOff = firstHop is not null;

            if (handedOff) _drain = Task.Run(DrainAsync, CancellationToken.None);
            else           listener.Stop();

            return new BrowserResult { ResultType = BrowserResultType.Success, Response = query };
        } finally {
            if (!handedOff) TearDown();
        }
    }

    /// <summary>
    /// Waits for the browser's return hop, hands its query to the join, and serves the same page.
    /// Never throws: an exception escaping to the NativeAOT runtime aborts the process, and this
    /// runs on a thread-pool thread nobody is watching.
    /// </summary>
    async Task DrainAsync() {
        var listener = _listener;
        if (listener is null) return;

        try {
            using var cts = new CancellationTokenSource(DrainCap);

            while (true) {
                var getContext = listener.GetContextAsync();
                HttpListenerContext ctx;

                try {
                    ctx = await getContext.WaitAsync(cts.Token);
                } catch (Exception) {
                    Observe(getContext);

                    return;
                }

                // The PATH alone does not make this our return hop — this port is reachable by any
                // local process, browser tab or web page. Only the join accepting it does. Ending
                // the wait on the path would mean one junk request killed the bridge and the real
                // browser arrived at a closed socket, which is exactly what the join's own refusal
                // to consume itself on a mismatch exists to prevent.
                if (ctx.Request.Url?.AbsolutePath == "/joined" && Accepted(ctx)) {
                    try { await WritePageAsync(ctx, success: true, redirectTo: null, joined: true); } catch { }

                    return;
                }

                // Anything else — a favicon, a stray probe, a /joined we did not accept — is
                // answered and ignored, and the wait continues under the same cap.
                try {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                } catch { }
            }
        } catch { } finally {
            TearDown();
        }
    }

    /// <summary>
    /// Called by whoever constructed this browser, at the end of their auth flow. Gives a pending
    /// drain a bounded window, then reclaims the listener by stopping it — which cancels the
    /// drain's pending accept, rather than abandoning a task that would keep the port alive.
    /// Idempotent, and safe when <see cref="InvokeAsync"/> never ran.
    /// <para>For <c>kcap setup</c> the drain finished minutes earlier — the user is at an
    /// interactive prompt — so the wait costs nothing.</para>
    /// </summary>
    public void Dispose() {
        var drain = _drain;

        try { drain?.Wait(DisposeWait); } catch { }
        TearDown();
        try { drain?.Wait(DisposeWait); } catch { }
    }

    // The single teardown. Both the drain and Dispose attempt the transition; only the winner tears
    // down, so the listener is stopped and disposed exactly once on every path.
    void TearDown() {
        if (Interlocked.Exchange(ref _tornDown, 1) != 0) return;

        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
    }

    /// <summary>
    /// Does this callback prove it came from the browser we sent out? The authorize URL carries an
    /// unguessable <c>state</c>, generated per login by OidcClient, and only the redirect it produced
    /// can echo it. Fails CLOSED: no state on either side means nothing to authenticate against, so
    /// the key is withheld rather than treating "nothing to compare" as a match.
    /// <para>Never throws — a malformed <c>StartUrl</c> costs the redirect, not the sign-in.</para>
    /// </summary>
    static bool StateEchoed(BrowserOptions options, string callbackQuery) {
        try {
            var expected = Param(new Uri(options.StartUrl).Query, "state");

            return expected is not null && Param(callbackQuery, "state") == expected;
        } catch {
            return false;
        }
    }

    // Hand-rolled rather than HttpUtility.ParseQueryString, which is not in the AOT-friendly surface
    // this assembly sticks to. Mirrors SetupJoin's own parser: an empty value reads as absent.
    static string? Param(string query, string name) {
        foreach (var pair in query.TrimStart('?').Split('&')) {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(pair[..eq], name, StringComparison.Ordinal)) continue;

            var raw = pair[(eq + 1)..];

            return raw.Length == 0 ? null : Uri.UnescapeDataString(raw);
        }

        return null;
    }

    // A faulted accept task with no continuation is an unobserved exception. Preserve the
    // discipline the timeout path has always had, for the drain's own accept too.
    static void Observe(Task<HttpListenerContext> accept) =>
        _ = accept.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // A join that throws is treated as a refusal, so a telemetry fault cannot end the wait early
    // any more than it can break authentication.
    bool Accepted(HttpListenerContext ctx) {
        try {
            return _join?.Accept(ctx.Request.Url?.Query ?? "") == true;
        } catch {
            return false;
        }
    }

    // Telemetry must never break authentication: a join that throws costs the redirect and
    // nothing else.
    string? SafeFirstHop(int port) {
        try {
            return _join?.FirstHopUrl(port);
        } catch {
            return null;
        }
    }

    /// <summary>
    /// The closing page, and the identical page served at <c>/joined</c>.
    /// <para><c>Referrer-Policy: no-referrer</c> is a SECURITY control on <c>/callback</c>, not
    /// hygiene: that URL's query carries the OAuth <c>code</c> and <c>state</c>, and a browser
    /// configured with <c>unsafe-url</c> would otherwise put the authorization code into a
    /// <c>Referer</c> header on the cross-origin hop — i.e. into our own access logs.
    /// <c>Cache-Control: no-store</c> keeps the join key and the web ids out of the disk cache, and
    /// <c>/joined</c>'s <c>history.replaceState</c> keeps them out of browser history.</para>
    /// <para>A JS <c>location.replace</c> rather than a 302: the person reads "Authentication
    /// successful!" FIRST, a no-JS browser simply stays on it, and a top-level GET navigation is
    /// what makes <c>SameSite=Lax</c> cookies travel — an img beacon or fetch would not.</para>
    /// </summary>
    static async Task WritePageAsync(HttpListenerContext ctx, bool success, string? redirectTo, bool joined) {
        var (title, message) = success
            ? ("Authentication successful!", "You can close this window and return to the terminal.")
            : ("Authentication failed", "Return to the terminal for details.");

        // JsonEncodedText escapes <, > and & , so a </script> break-out or an escape out of the JS
        // string literal is structurally impossible — and it needs no reflection, so it stays
        // AOT-clean.
        var script =
            redirectTo is not null ? $"<script>location.replace(\"{JsonEncodedText.Encode(redirectTo)}\")</script>"
            : joined               ? "<script>history.replaceState({}, \"\", \"/joined\")</script>"
            : "";

        var html = $"<html><body style='font-family:system-ui;max-width:480px;margin:80px auto;text-align:center'>"
          + $"<h2>{WebUtility.HtmlEncode(title)}</h2><p>{WebUtility.HtmlEncode(message)}</p>{script}</body></html>";

        var buffer = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType     = "text/html";
        ctx.Response.ContentLength64 = buffer.Length;
        ctx.Response.AddHeader("Referrer-Policy", "no-referrer");
        ctx.Response.AddHeader("Cache-Control", "no-store");
        await ctx.Response.OutputStream.WriteAsync(buffer);
        ctx.Response.Close();
    }
}

/// <summary>No browser could be launched on this machine. Callers with a device-code rung take it.</summary>
public sealed class BrowserLaunchException : Exception {
    public BrowserLaunchException() : base("Could not launch a browser on this machine.") { }

    public BrowserLaunchException(string message) : base(message) { }

    public BrowserLaunchException(string message, Exception innerException) : base(message, innerException) { }
}
