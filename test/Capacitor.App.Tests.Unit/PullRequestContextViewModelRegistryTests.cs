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

    static readonly PullRequestRepository Primary = new("github", "github.com", "example", "repo", "hash");

    [Test]
    public Task A_missing_tool_shows_the_install_note_before_any_pr_is_linked() => RunOnUiAsync(async () => {
        var h = new Harness("github.com", Primary);
        h.Provider.Status = PullRequestReaderStatusKind.ToolMissing;
        h.Links.Links = [];
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => !h.Vm.IsReading && h.Vm.HasReaderNote, what: "note shown");
        await Assert.That(h.Vm.ReaderNote).IsEqualTo("Install GitHub CLI to read pull requests here.");
        await Assert.That(h.Vm.ShowsInstallTool).IsTrue();
        await Assert.That(h.Vm.InstallToolLabel).IsEqualTo("Install GitHub CLI");
        await Assert.That(h.Vm.ShowsSignIn).IsFalse();
        await Assert.That(h.Vm.ShowsLinkGitHub).IsFalse();
        await h.Vm.InstallToolCommand.Execute();
        await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://cli.github.com" });
        await h.Dispose();
    });

    [Test]
    public Task A_signed_out_tool_names_the_sign_in_command_and_recheck_clears_the_note_once_ready() => RunOnUiAsync(async () => {
        var h = new Harness("github.com", Primary);
        h.Provider.Status = PullRequestReaderStatusKind.SignedOut;
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => !h.Vm.IsReading && h.Vm.HasReaderNote, what: "note shown");
        await Assert.That(h.Vm.ReaderNote).IsEqualTo("GitHub CLI is not signed in. Run gh auth login to read pull requests here.");
        await Assert.That(h.Vm.ShowsInstallTool).IsFalse();
        h.Provider.Status = PullRequestReaderStatusKind.Ready;
        h.Time.Advance(TimeSpan.FromSeconds(16));
        await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "rechecked and reading");
        await Assert.That(h.Vm.HasReaderNote).IsFalse();
        await h.Dispose();
    });

    [Test]
    public Task A_selected_pr_on_a_host_the_tool_is_not_signed_in_to_names_that_host() => RunOnUiAsync(async () => {
        var h = new Harness("github.com", Primary);
        h.Links.Links = [Link("ghe.example", 4)];
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => !h.Vm.IsReading && h.Vm.HasChoice, what: "list applied");
        await Assert.That(h.Vm.ReaderNote).IsEqualTo("GitHub CLI is not signed in for ghe.example. Run gh auth login --hostname ghe.example to read it here.");
        await Assert.That(h.Vm.ShowsInstallTool).IsFalse();
        await h.Dispose();
    });

    [Test]
    public Task No_note_shows_while_a_provider_serves_the_session_host() => RunOnUiAsync(async () => {
        var h = new Harness("github.com", Primary);
        h.Push(); await h.Show();
        await Assert.That(h.Vm.HasReaderNote).IsFalse();
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
