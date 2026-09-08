using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers;

public class PullRequestReaderRegistryTests {
    static PullRequestSubjectDto Subject(string host = "github.com", string provider = "github", int number = 1) => new() {
        Provider = provider, Host = host, RepoHash = "hash", Owner = "example", RepoName = "repo", Number = number };
    static PullRequestLinkDto Link(string host, int number, string provider = "github", string? url = null) => new() {
        Provider = provider, Host = host, RepoHash = "hash", Owner = "example", RepoName = "repo", Number = number,
        Url = url ?? $"https://{host}/example/repo/pull/{number}", HeadRef = "feature" };

    [Test]
    public async Task Reads_route_to_the_first_ready_provider_that_serves_the_host() {
        var first = new StubProvider("first", ready: true, hosts: ["ghe.example"]);
        var second = new StubProvider("second", ready: true, hosts: ["github.com", "ghe.example"]);
        var registry = new PullRequestReaderRegistry(new StubLinks(), [first, second]);
        await registry.DiscoverAsync(false, default);
        await registry.OverviewAsync("session", Subject("ghe.example"), default);
        await registry.OverviewAsync("session", Subject(), default);
        await Assert.That(first.Overviews).IsEqualTo(1);
        await Assert.That(second.Overviews).IsEqualTo(1);
    }

    [Test]
    public async Task A_subject_no_provider_serves_reads_as_unavailable_with_no_reader() {
        var registry = new PullRequestReaderRegistry(new StubLinks(), [new StubProvider("gh", ready: true, hosts: ["github.com"])]);
        await registry.DiscoverAsync(false, default);
        var read = await registry.OverviewAsync("session", Subject("gitlab.com", "gitlab"), default);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Unavailable);
        await Assert.That(read.Reason).IsEqualTo("no_reader");
        await Assert.That(read.AccessFailure).IsEqualTo("invalid");
    }

    [Test]
    public async Task Capability_is_supported_when_any_provider_is_ready_else_the_session_link_capability() {
        var links = new StubLinks { Capability = new(PullRequestCapabilityKind.Legacy) };
        var provider = new StubProvider("gh", ready: false, hosts: []);
        var registry = new PullRequestReaderRegistry(links, [provider]);
        await Assert.That((await registry.DiscoverAsync(false, default)).Kind).IsEqualTo(PullRequestCapabilityKind.Legacy);
        provider.Ready = true;
        await Assert.That((await registry.DiscoverAsync(true, default)).Kind).IsEqualTo(PullRequestCapabilityKind.Supported);
    }

    [Test]
    public async Task Legacy_links_are_parsed_into_subjects_by_the_provider_that_recognizes_them() {
        var links = new StubLinks { Capability = new(PullRequestCapabilityKind.Legacy),
            Legacy = [Link("github.com", 7, provider: "unknown"), Link("gitlab.com", 8, provider: "unknown", url: "https://gitlab.com/example/repo/-/merge_requests/8")] };
        var gh = new StubProvider("gh", ready: true, hosts: ["github.com"]);
        var lab = new StubProvider("lab", ready: true, hosts: ["gitlab.com"], kind: "gitlab", linkShape: "/-/merge_requests/");
        var registry = new PullRequestReaderRegistry(links, [gh, lab]);
        await registry.DiscoverAsync(false, default);
        var list = await registry.ListAsync("session", default);
        await Assert.That(list.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(list.Data!.Items.Select(item => item.Provider).ToArray()).IsEquivalentTo(new[] { "github", "gitlab" });
        await Assert.That(list.Data.Items[1].Number).IsEqualTo(8);
        await Assert.That(registry.PrLink("https://gitlab.com/example/repo/-/merge_requests/8", PullRequestWire.Subject(list.Data.Items[1]))).IsNotNull();
    }

    [Test]
    public async Task Live_discovery_merges_with_session_links_deduplicated_and_canonically_ordered() {
        var links = new StubLinks { Links = [Link("github.com", 5)] };
        var gh = new StubProvider("gh", ready: true, hosts: ["github.com"]) { Discovered = [Link("github.com", 5), Link("github.com", 2)] };
        var registry = new PullRequestReaderRegistry(links, [gh]);
        await registry.DiscoverAsync(false, default);
        registry.DescribeSession("session", new("github", "github.com", "example", "repo", "hash"), "feature");
        var list = await registry.ListAsync("session", default);
        await Assert.That(list.Data!.Items.Select(item => item.Number).ToArray()).IsEquivalentTo(new[] { 2, 5 });
        await Assert.That(gh.DiscoverCalls).IsEqualTo(1);
        registry.ResetSession("session");
        await registry.ListAsync("session", default);
        await Assert.That(gh.DiscoverCalls).IsEqualTo(1);
    }

    [Test]
    public async Task A_provider_change_on_rediscovery_restarts_the_next_read_once() {
        var provider = new StubProvider("gh", ready: false, hosts: ["github.com"]);
        var server = new StubProvider("server", ready: true, hosts: ["github.com"]);
        var registry = new PullRequestReaderRegistry(new StubLinks(), [provider, server]);
        await registry.DiscoverAsync(false, default);
        await registry.OverviewAsync("a", Subject(), default);
        await registry.OverviewAsync("b", Subject(), default);
        provider.Ready = true;
        await registry.DiscoverAsync(true, default);
        foreach (var sessionId in new[] { "a", "b" }) {
            var restart = await registry.OverviewAsync(sessionId, Subject(), default);
            await Assert.That(restart.Kind).IsEqualTo(PullRequestReadKind.Restart);
            await Assert.That(restart.Reason).IsEqualTo("integration_changed");
            await Assert.That((await registry.OverviewAsync(sessionId, Subject(), default)).Kind).IsEqualTo(PullRequestReadKind.Ready);
        }
        await Assert.That(provider.Overviews).IsEqualTo(2);
    }

    [Test]
    public async Task Notes_describe_the_missing_or_signed_out_tool_for_a_host_and_nothing_when_served() {
        var gh = new StubProvider("gh", ready: false, hosts: [], status: PullRequestReaderStatusKind.ToolMissing);
        var registry = new PullRequestReaderRegistry(new StubLinks(), [gh]);
        await registry.DiscoverAsync(false, default);
        await Assert.That(registry.NoteFor("github", "github.com")!.Text).IsEqualTo("Install GitHub CLI to read pull requests here.");
        await Assert.That(registry.NoteFor("github", "github.com")!.InstallUrl).IsEqualTo("https://cli.github.com");
        gh.Status = PullRequestReaderStatusKind.SignedOut;
        await registry.DiscoverAsync(true, default);
        await Assert.That(registry.NoteFor("github", "github.com")!.Text).IsEqualTo("GitHub CLI is not signed in. Run gh auth login to read pull requests here.");
        gh.Ready = true; gh.Hosts = ["github.com"];
        await registry.DiscoverAsync(true, default);
        await Assert.That(registry.NoteFor("github", "github.com")).IsNull();
        await Assert.That(registry.NoteFor("github", "ghe.example")!.Text).IsEqualTo("GitHub CLI is not signed in for ghe.example. Run gh auth login --hostname ghe.example to read it here.");
        await Assert.That(registry.NoteFor("gitlab", "gitlab.com")).IsNull();
    }

    internal sealed class StubLinks : IPullRequestSource {
        public PullRequestCapability Capability = new(PullRequestCapabilityKind.Supported, 1);
        public PullRequestLinkDto[] Links = [];
        public PullRequestLinkDto[] Legacy = [];
        public Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct) => Task.FromResult(Capability);
        public void ResetSession(string sessionId) { }
        public Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(new PullRequestRead<PullRequestLinkListDto>(PullRequestReadKind.Ready, new() { Items = Links }, FetchedAt: DateTime.UtcNow));
        public Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(new PullRequestRead<PullRequestLinkListDto>(PullRequestReadKind.Ready, new() { Items = Legacy }, FetchedAt: DateTime.UtcNow));
        public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) => throw new NotSupportedException();
        public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section, string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class => throw new NotSupportedException();
    }

    internal sealed class StubProvider(string name, bool ready, string[] hosts, string kind = "github", string linkShape = "/pull/",
            PullRequestReaderStatusKind status = PullRequestReaderStatusKind.SignedOut) : IPullRequestReaderProvider {
        public bool Ready = ready;
        public string[] Hosts = hosts;
        public PullRequestReaderStatusKind Status = status;
        public int Overviews, DiscoverCalls;
        public PullRequestLinkDto[] Discovered = [];
        public string Name => name;
        public string ProviderKind => kind;
        public PullRequestReaderTool? Tool => kind == "github"
            ? new("GitHub CLI", "https://cli.github.com", host => host is null ? "gh auth login" : "gh auth login --hostname " + host)
            : new("GitLab CLI", "https://gitlab.com/gitlab-org/cli", host => host is null ? "glab auth login" : "glab auth login --hostname " + host);
        public Task<PullRequestReaderStatus> ProbeAsync(bool refresh, CancellationToken ct)
            => Task.FromResult(new PullRequestReaderStatus(Ready ? PullRequestReaderStatusKind.Ready : Status));
        public bool Serves(string provider, string host) => Ready && provider == kind && Hosts.Contains(host);
        public PullRequestSubjectDto? ParseLink(string? url) {
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !Hosts.Contains(uri.Host)) return null;
            var parts = uri.AbsolutePath.Split(linkShape, 2);
            if (parts.Length != 2 || !int.TryParse(parts[1].TrimEnd('/'), out var number)) return null;
            var repo = parts[0].Trim('/').Split('/');
            return new() { Provider = kind, Host = uri.Host, RepoHash = "hash", Owner = repo[0], RepoName = repo[1], Number = number };
        }
        public string? PrLink(string? url, PullRequestSubjectDto subject) => ParseLink(url) == subject ? url : null;
        public Task<IReadOnlyList<PullRequestLinkDto>> DiscoverAsync(PullRequestRepository repository, string branch, CancellationToken ct) {
            DiscoverCalls++;
            return Task.FromResult<IReadOnlyList<PullRequestLinkDto>>(Discovered);
        }
        public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) {
            Overviews++;
            return Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Ready, new() { Title = name }, subject, DateTime.UtcNow, AccessValidForSeconds: 30));
        }
        public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section, string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class
            => throw new NotSupportedException();
        public void ResetSession(string sessionId) { }
    }
}
