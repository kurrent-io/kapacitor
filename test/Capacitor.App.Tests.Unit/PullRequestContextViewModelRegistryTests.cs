using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class PullRequestContextViewModelRegistryTests {
    static PullRequestLinkDto Link(string host, int number) => new() { Provider = "github", Host = host, RepoHash = "hash", Owner = "example", RepoName = "repo",
        Number = number, Url = $"https://{host}/example/repo/pull/{number}", Title = "Linked PR", HeadRef = "feature" };

    [Test]
    public Task A_subject_on_an_enterprise_host_reads_and_opens_through_the_registry() => RunOnUiAsync(async () => {
        var h = new Harness("ghe.example");
        h.Links.Links = [Link("ghe.example", 4)];
        h.Push(); await h.Show();
        await Assert.That(h.Vm.Title).IsEqualTo("Local PR");
        h.Vm.SetReaderVisible(true);
        await Assert.That(h.Vm.Description).IsEqualTo("Local description");
        await h.Vm.OpenGitHubCommand.Execute();
        await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://ghe.example/example/repo/pull/4" });
        await h.Dispose();
    });

    [Test]
    public Task The_session_is_described_to_the_registry_so_live_discovery_can_run() => RunOnUiAsync(async () => {
        var h = new Harness("github.com", new PullRequestRepository("github", "github.com", "example", "repo", "hash"));
        h.Provider.Discovered = [Link("github.com", 9)];
        h.Push(); await h.Show();
        await Assert.That(h.Provider.Discoveries.Count).IsEqualTo(1);
        await Assert.That(h.Provider.Discoveries[0].Branch).IsEqualTo("feature");
        await Assert.That(h.Provider.Discoveries[0].Repository.Owner).IsEqualTo("example");
        await Assert.That(h.Vm.Choices.Select(choice => choice.Subject.Number).ToArray()).IsEquivalentTo(new[] { 1, 2, 9 });
        await h.Dispose();
    });

    [Test]
    public Task A_subject_no_provider_serves_shows_the_no_reader_notice_without_the_capacitor_sign_in() => RunOnUiAsync(async () => {
        var h = new Harness("github.com");
        h.Links.Links = [Link("ghe.example", 4)];
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => !h.Vm.IsReading && h.Vm.HasChoice, what: "list applied");
        await Assert.That(h.Vm.CanReveal).IsFalse();
        await Assert.That(h.Vm.Notice).IsEqualTo("No reader is available for this pull request's host.");
        await Assert.That(h.Vm.ShowsSignIn).IsFalse();
        await h.Dispose();
    });

    sealed class Harness {
        internal BehaviorSubject<AgentStatusDto?> Presence { get; } = new(null);
        internal FakeTimeProvider Time { get; } = new();
        internal FakePullRequestSource Links { get; }
        internal StubReaderProvider Provider { get; }
        internal PullRequestReaderRegistry Registry { get; }
        internal RecordingOpener Opener { get; } = new();
        internal PullRequestContextViewModel Vm { get; }
        internal Harness(string host, PullRequestRepository? primary = null) {
            Links = new(Time);
            Provider = new(Time, host);
            Registry = new(Links, [Provider]);
            Vm = new(Presence, Registry, Time, Opener, () => { }, primaryRepo: () => primary);
        }
        internal void Push() => Presence.OnNext(Agent("agent", "claude", hasTerminal: false, sessionId: "session", branch: "feature"));
        internal async Task Show() { Vm.SetForeground(true); await WaitUntilAsync(() => Vm.CanReveal, what: "PR overview admitted"); }
        internal async Task Dispose() { await Vm.TeardownAsync(); Presence.Dispose(); }
    }
}
