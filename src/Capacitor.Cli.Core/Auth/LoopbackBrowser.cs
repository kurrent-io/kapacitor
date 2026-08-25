using System.Net;
using System.Text;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// OidcClient <see cref="IBrowser"/> backed by a 127.0.0.1 loopback HttpListener.
/// Opens the system browser to the authorize URL, waits for the redirect callback,
/// and returns its raw query string. WorkOS documents the loopback exception as
/// 127.0.0.1 (not localhost). The bind exception is intentionally NOT caught so the
/// GitHub flow can fall back to device flow on a bind failure. A caller cancel throws
/// <see cref="OperationCanceledException"/>; only the independent timeout returns Timeout.
/// </summary>
/// <param name="hint">
/// An escape hatch offered while the wait runs, printed under the "visit:" line rather than before it:
/// above, it reads as an alternative to signing in at all instead of an alternative to that URL.
/// </param>
public sealed class LoopbackBrowser(
        IBrowserLauncher launcher,
        IAuthProgress?   progress = null,
        string?          hint     = null
    ) : IBrowser {
    readonly IAuthProgress _progress = progress ?? ConsoleAuthProgress.Instance;

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken ct = default) {
        var port = new Uri(options.EndUrl).Port;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start(); // bind failure propagates (HttpListenerException / PlatformNotSupportedException)

        // Bind, launch, THEN announce: nothing is said until there is something true to say, or a
        // failed launch has already printed the browser narrative and a 300-character authorize URL
        // for a route the reader cannot take. (The listener still binds first, so a fast browser
        // cannot beat it.)
        //
        // Thrown rather than waited out: with no browser here, the callback can only be reached from a
        // browser on this machine, and there isn't one. Five minutes of listening ends in the same
        // place, having offered a URL that leads to a connection refused.
        if (!launcher.TryOpen(options.StartUrl)) throw new BrowserLaunchException();

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
                _ = getContext.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                // The caller's own cancel is not a timeout — it propagates so the flow answers Cancelled.
                ct.ThrowIfCancellationRequested();

                return new BrowserResult { ResultType = BrowserResultType.Timeout };
            }

            if (context.Request.Url?.AbsolutePath == "/callback") break;

            // Ignore favicon and other browser-issued requests that aren't our callback.
            context.Response.StatusCode = 404;
            context.Response.Close();
        }

        var query = context.Request.Url?.Query ?? "";
        await WriteClosingPageAsync(context, success: !query.Contains("error="));
        listener.Stop();

        return new BrowserResult { ResultType = BrowserResultType.Success, Response = query };
    }

    static async Task WriteClosingPageAsync(HttpListenerContext ctx, bool success) {
        var (title, message) = success
            ? ("Authentication successful!", "You can close this window and return to the terminal.")
            : ("Authentication failed", "Return to the terminal for details.");

        var html = $"<html><body style='font-family:system-ui;max-width:480px;margin:80px auto;text-align:center'>"
          + $"<h2>{WebUtility.HtmlEncode(title)}</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";

        var buffer = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType     = "text/html";
        ctx.Response.ContentLength64 = buffer.Length;
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
