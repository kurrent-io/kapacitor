using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class PullRequestContextViewModelTests {
    [Test]
    public Task Reads_start_only_in_the_foreground_and_focus_loss_masks_already_loaded_bodies() => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Push();
        await Assert.That(h.Source.Lists).IsEqualTo(0);
        await h.Show();
        h.Vm.SetReaderVisible(true);
        await Assert.That(h.Vm.Description).IsEqualTo("Private description");
        h.Vm.SetForeground(false);
        await Assert.That(h.Vm.Description).IsNull();
        await Assert.That(h.Vm.Rows).IsEmpty();
        await Assert.That(h.Vm.CanReveal).IsFalse();
        var calls = h.Source.Overviews;
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Dispatcher.UIThread.RunJobs();
        await Assert.That(h.Source.Overviews).IsEqualTo(calls);
        await h.Dispose();
    });

    [Test]
    public Task A_B_A_selection_cancels_the_old_request_and_rejects_its_late_result() => RunOnUiAsync(async () => {
        var h = new Harness();
        var old = new TaskCompletionSource<PullRequestRead<PullRequestOverviewDto>>();
        h.Source.OverviewResponses.Enqueue((_, _) => old.Task);
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => h.Source.Overviews == 1, what: "first overview request");
        var a = h.Vm.Choices[0]; var b = h.Vm.Choices[1];
        h.Vm.Selected = b;
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "B admitted");
        h.Source.OverviewTitle = "Current A";
        h.Vm.Selected = a;
        await WaitUntilAsync(() => h.Vm.Title == "Current A", what: "new A admitted");
        old.SetResult(h.Source.Overview(a.Subject, "Old A"));
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "old request settles");
        Dispatcher.UIThread.RunJobs();
        await Assert.That(h.Source.OverviewTokens[0].IsCancellationRequested).IsTrue();
        await Assert.That(h.Vm.Title).IsEqualTo("Current A");
        await h.Dispose();
    });

    [Test]
    public Task Transient_grace_retains_only_the_current_view_and_cannot_open_a_new_section() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show();
        h.Vm.SetReaderVisible(true);
        h.Source.Failure = "transient";
        h.Time.Advance(TimeSpan.FromSeconds(21));
        await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "access refresh fails");
        await Assert.That(h.Vm.CanReveal).IsFalse();
        await Assert.That(h.Vm.Description).IsEqualTo("Private description");
        await h.Vm.ShowSectionCommand.Execute("reviews");
        await Assert.That(h.Source.Pages).IsEqualTo(0);
        h.Vm.SetReaderVisible(false); h.Vm.SetReaderVisible(true);
        await Assert.That(h.Vm.Description).IsNull();
        h.Time.Advance(TimeSpan.FromMinutes(6)); Dispatcher.UIThread.RunJobs();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "grace expires");
        await Assert.That(h.Vm.CanDisplay).IsFalse();
        await h.Dispose();
    });

    [Test]
    public Task An_explicit_denial_clears_previously_visible_content_immediately() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        h.Source.Failure = "denied"; h.Time.Advance(TimeSpan.FromSeconds(16));
        await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "denial applied");
        await Assert.That(h.Vm.Description).IsNull();
        await Assert.That(h.Vm.CanDisplay).IsFalse();
        await Assert.That(h.Vm.Notice).Contains("cannot read");
        await h.Dispose();
    });

    [Test]
    public Task Explicit_selection_survives_list_refreshes_and_body_state_does_not_survive_PR_switches() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        h.Vm.Selected = h.Vm.Choices[1];
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "selected second PR");
        await h.Vm.ShowSectionCommand.Execute("conversation");
        await WaitUntilAsync(() => h.Vm.Rows.Count == 1, what: "comment page loaded");
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "list refresh");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        h.Vm.Selected = h.Vm.Choices[0];
        await Assert.That(h.Vm.Rows).IsEmpty();
        await h.Dispose();
    });

    [Test]
    public Task A_section_keeps_eight_pages_and_exposes_a_reload_control_for_evicted_content() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Source.TotalPages = 12; h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        await h.Vm.ShowSectionCommand.Execute("conversation");
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "first page");
        for (var page = 1; page < 10; page++) {
            await h.Vm.LoadMoreCommand.Execute();
            await WaitUntilAsync(() => !h.Vm.IsReading, what: "next page");
        }
        await Assert.That(h.Vm.Rows.Count).IsEqualTo(8);
        await Assert.That(h.Vm.CanReloadEarlier).IsTrue();
        await Assert.That(h.Vm.HasMore).IsTrue();
        await h.Vm.ReloadEarlierCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "evicted page reloaded");
        await Assert.That(h.Vm.Rows[0].Id).IsEqualTo("item-1");
        await Assert.That(h.Vm.Rows.Count).IsEqualTo(8);
        await h.Vm.LoadMoreCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "resume after reloading earlier");
        await Assert.That(h.Vm.Rows[^1].Id).IsEqualTo("item-9");
        await h.Dispose();
    });

    [Test]
    public Task Complete_current_checks_take_precedence_over_a_conflicting_advisory_rollup() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Source.TotalPages = 1; h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        await Assert.That(h.Vm.CheckSummary).Contains("GitHub summary");
        await h.Vm.ShowSectionCommand.Execute("checks");
        await WaitUntilAsync(() => h.Vm.Rows.Count == 1, what: "checks page");
        await Assert.That(h.Vm.CheckSummary).Contains("1 failed");
        await Assert.That(h.Vm.CheckSummary).DoesNotContain("successful");
        await h.Dispose();
    });

    [Test]
    public Task Retry_refreshes_the_current_section_after_access_recovers() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        await h.Vm.ShowSectionCommand.Execute("reviews");
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "initial reviews");
        var pages = h.Source.Pages;
        h.Source.Failure = "transient"; h.Time.Advance(TimeSpan.FromSeconds(16));
        await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "failed renewal");
        await Assert.That(h.Vm.CanReveal).IsFalse();
        h.Source.Failure = null; h.Time.Advance(TimeSpan.FromSeconds(16));
        await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "recovered renewal");
        await Assert.That(h.Vm.CanReveal).IsTrue();
        await Assert.That(h.Source.Pages).IsEqualTo(pages + 1);
        await h.Dispose();
    });

    [Test]
    public Task A_late_primary_repository_hint_corrects_the_default_without_changing_an_explicit_choice() => RunOnUiAsync(async () => {
        string? primary = null;
        var h = new Harness(() => primary);
        h.Source.Links[1] = h.Source.Links[1] with { RepoHash = "primary" };
        h.Push(); await h.Show();
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
        primary = "primary";
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "primary repository selected");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        h.Vm.Selected = h.Vm.Choices[0];
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "explicit selection admitted");
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "explicit selection retained");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
        await h.Dispose();
    });

    [Test]
    public Task An_unlinked_explicit_selection_stays_unavailable_until_it_returns_or_the_user_selects_another_PR() => RunOnUiAsync(async () => {
        var h = new Harness(); h.Push(); await h.Show(); h.Vm.SetReaderVisible(true);
        h.Vm.Selected = h.Vm.Choices[1];
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "explicit PR admitted");
        var removed = h.Source.Links[1];
        h.Source.Links = [h.Source.Links[0]];
        var overviews = h.Source.Overviews;
        for (var refresh = 0; refresh < 2; refresh++) {
            h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
            await WaitUntilAsync(() => !h.Vm.IsReading, what: "missing selection applied");
            await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
            await Assert.That(h.Vm.Selected.IsAvailable).IsFalse();
            await Assert.That(h.Vm.Description).IsNull();
            await Assert.That(h.Vm.CanReveal).IsFalse();
            await Assert.That(h.Source.Overviews).IsEqualTo(overviews);
        }
        await h.Vm.OpenGitHubCommand.Execute();
        await Assert.That(h.Opener.Opened).IsEmpty();
        h.Source.Links = [h.Source.Links[0], removed];
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "relinked selection admitted");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(2);
        await Assert.That(h.Vm.Selected.IsAvailable).IsTrue();
        h.Source.Links = [h.Source.Links[0]];
        h.Time.Advance(TimeSpan.FromSeconds(16)); await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => !h.Vm.IsReading, what: "selection removed again");
        h.Vm.Selected = h.Vm.Choices.Single(choice => choice.Subject.Number == 1);
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "replacement explicitly selected");
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
        await h.Dispose();
    });

    [Test]
    [Arguments("https://example.com/example/repo/pull/1", false)]
    [Arguments("https://github.com/example/repo/pull/99", false)]
    [Arguments("https://github.com/example/other/pull/1", false)]
    [Arguments("https://github.com/example/repo/pull/1", true)]
    public Task The_main_GitHub_action_opens_only_the_selected_PR(string url, bool accepted) => RunOnUiAsync(async () => {
        var h = new Harness();
        h.Source.Links[0] = h.Source.Links[0] with { Url = url };
        h.Push(); await h.Show();
        await h.Vm.OpenGitHubCommand.Execute();
        await Assert.That(h.Opener.Opened.Count).IsEqualTo(accepted ? 1 : 0);
        if (accepted) await Assert.That(h.Opener.Opened[0]).IsEqualTo(url);
        await h.Dispose();
    });

    sealed class Harness {
        internal BehaviorSubject<AgentStatusDto?> Presence { get; } = new(null);
        internal FakeTimeProvider Time { get; } = new();
        internal FakePullRequestSource Source { get; }
        internal RecordingOpener Opener { get; } = new();
        internal PullRequestContextViewModel Vm { get; }
        internal Harness(Func<string?>? primary = null) {
            Source = new(Time);
            Vm = new(Presence, Source, Time, Opener, () => { }, primaryRepo: primary);
        }
        internal void Push() => Presence.OnNext(Agent("agent", "claude", hasTerminal: false, sessionId: "session", branch: "feature"));
        internal async Task Show() { Vm.SetForeground(true); await WaitUntilAsync(() => Vm.CanReveal, what: "PR overview admitted"); }
        internal async Task Dispose() { await Vm.TeardownAsync(); Presence.Dispose(); }
    }
}
