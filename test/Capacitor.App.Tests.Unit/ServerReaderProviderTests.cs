using System.Net;
using System.Text;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;

namespace Capacitor.App.Tests.Unit;

public class ServerReaderProviderTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly PullRequestSubjectDto Subject = new() { Provider = "github", Host = "github.com", RepoHash = "hash", Owner = "example", RepoName = "repo", Number = 1 };

    [Test]
    public async Task Probe_maps_the_server_capability_and_serves_github_com_only_while_supported() {
        using var handler = new Handler { Versions = "[1]" };
        await using var source = new ServerPullRequestSource(Config.Root, Resolutions.At("https://server.test", Config.Root),
            (_, _, _, _) => Task.FromResult((new HttpClient(handler), AuthStatus.Ok)));
        var provider = new ServerReaderProvider(source);
        await Assert.That(provider.Name).IsEqualTo("server");
        await Assert.That(provider.ProviderKind).IsEqualTo("github");
        await Assert.That(provider.Tool).IsNull();
        await Assert.That(provider.Serves("github", "github.com")).IsFalse();
        var status = await provider.ProbeAsync(false, default);
        await Assert.That(status.Kind).IsEqualTo(PullRequestReaderStatusKind.Ready);
        await Assert.That(provider.Serves("github", "github.com")).IsTrue();
        await Assert.That(provider.Serves("github", "ghe.example")).IsFalse();
        await Assert.That(provider.ParseLink("https://github.com/example/repo/pull/1")).IsNull();
        await Assert.That(await provider.DiscoverAsync(new("github", "github.com", "example", "repo", "hash"), "feature", default)).IsEmpty();
        await Assert.That(provider.PrLink("https://github.com/example/repo/pull/1", Subject)).IsEqualTo("https://github.com/example/repo/pull/1");
        await Assert.That((await provider.OverviewAsync("session", Subject, default)).Kind).IsEqualTo(PullRequestReadKind.Ready);
    }

    [Test]
    public async Task An_older_server_probes_as_failed_with_its_capability_named() {
        using var handler = new Handler { Versions = null };
        await using var source = new ServerPullRequestSource(Config.Root, Resolutions.At("https://server.test", Config.Root),
            (_, _, _, _) => Task.FromResult((new HttpClient(handler), AuthStatus.Ok)));
        var provider = new ServerReaderProvider(source);
        var status = await provider.ProbeAsync(false, default);
        await Assert.That(status.Kind).IsEqualTo(PullRequestReaderStatusKind.Failed);
        await Assert.That(status.Reason).IsEqualTo("Legacy");
        await Assert.That(provider.Serves("github", "github.com")).IsFalse();
    }

    sealed class Handler : HttpMessageHandler {
        internal string? Versions;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var discovery = request.RequestUri!.AbsolutePath == "/auth/config";
            var body = discovery ? (Versions is null ? """{"provider":"workos"}""" : $$$"""{"provider":"workos","pull_request_reads_versions":{{{Versions}}}}""")
                : """{"status":"ready","subject":{"provider":"github","host":"github.com","repo_hash":"hash","owner":"example","repo_name":"repo","number":1},"data":{"title":"Server PR"},"fetched_at":"2026-09-08T10:00:00Z","poll_after_seconds":30,"access_valid_for_seconds":30}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
