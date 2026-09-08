using System.Net;
using System.Text;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.Tests.Unit;

public class ServerPullRequestSourceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Three_missing_reads_stop_only_that_session_and_retry_resets_it() {
        using var handler = new Handler();
        await using var source = new ServerPullRequestSource(Config.Root, Resolutions.At("https://server.test", Config.Root),
            (_, _, _, _) => Task.FromResult((new HttpClient(handler), AuthStatus.Ok)));
        for (var i = 0; i < 4; i++) await source.ListAsync("missing", default);
        await Assert.That(handler.Lists).IsEqualTo(3);
        await Assert.That((await source.ListAsync("healthy", default)).Kind).IsEqualTo(PullRequestReadKind.Ready);
        source.ResetSession("missing");
        await source.ListAsync("missing", default);
        await Assert.That(handler.Lists).IsEqualTo(5);
    }

    [Test]
    public async Task Sign_in_during_client_creation_cannot_publish_the_old_authenticated_client() {
        using var handler = new Handler();
        var gate = new TaskCompletionSource<(HttpClient, AuthStatus)>();
        var calls = 0;
        await using var source = new ServerPullRequestSource(Config.Root, Resolutions.At("https://server.test", Config.Root), (_, _, _, _) => {
            calls++;
            return calls == 1 ? gate.Task : Task.FromResult((new HttpClient(new Handler()), AuthStatus.Ok));
        });
        var pending = source.DiscoverAsync(false, default);
        source.InvalidateAuthentication();
        gate.SetResult((new HttpClient(handler), AuthStatus.Ok));
        await Assert.That((await pending).Kind).IsEqualTo(PullRequestCapabilityKind.SignedOut);
        await Assert.That(handler.Disposed).IsTrue();
        await Assert.That((await source.DiscoverAsync(false, default)).Kind).IsEqualTo(PullRequestCapabilityKind.Supported);
        await Assert.That(calls).IsEqualTo(2);
    }

    sealed class Handler : HttpMessageHandler {
        internal int Lists;
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var path = request.RequestUri!.AbsolutePath;
            var discovery = path == "/auth/config";
            if (!discovery) Lists++;
            return Task.FromResult(new HttpResponseMessage(path.Contains("/missing/", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.OK) {
                RequestMessage = request,
                Content = new StringContent(discovery ? """{"provider":"workos","pull_request_reads_versions":[1]}"""
                    : """{"status":"ready","data":{"items":[]},"fetched_at":"2026-09-07T10:00:00Z","poll_after_seconds":30,"access_valid_for_seconds":0}""", Encoding.UTF8, "application/json")
            });
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
