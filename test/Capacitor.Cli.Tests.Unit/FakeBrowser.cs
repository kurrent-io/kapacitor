using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Test IBrowser: returns a canned callback query, or a non-success result.</summary>
public sealed class FakeBrowser(Func<string, BrowserResult> respond) : IBrowser {
    public string? LastStartUrl { get; private set; }

    public Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken ct = default) {
        LastStartUrl = options.StartUrl;
        return Task.FromResult(respond(options.StartUrl));
    }

    // Echo the state from the StartUrl so ProcessResponseAsync's state check passes.
    public static FakeBrowser WithCode(string code) => new(startUrl => {
        var query = new Uri(startUrl).Query.TrimStart('?');
        var state = query.Split('&')
            .First(p => p.StartsWith("state=", StringComparison.Ordinal))["state=".Length..];
        return new BrowserResult { ResultType = BrowserResultType.Success, Response = $"?code={code}&state={state}" };
    });

    public static FakeBrowser WithRawQuery(string query) =>
        new(_ => new BrowserResult { ResultType = BrowserResultType.Success, Response = query });

    public static FakeBrowser NonSuccess(BrowserResultType type) =>
        new(_ => new BrowserResult { ResultType = type });

    /// <summary>Cancels the caller mid-wait and answers non-success — a browser that renders a cancel as its own result type.</summary>
    public static FakeBrowser CancellingCaller(CancellationTokenSource cts, BrowserResultType type = BrowserResultType.Timeout) =>
        new(_ => {
            cts.Cancel();

            return new BrowserResult { ResultType = type };
        });
}
