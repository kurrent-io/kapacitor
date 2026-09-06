using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// RailSessionViewModel's OpenCommand and IsSelected OAPH both run through
/// RxSchedulers.MainThreadScheduler, which is not immediate in a bare test process — see
/// MainWindowViewModelTests' header comment. Every test here runs inside
/// AvaloniaSession.WithImmediateRxScheduler and carries [NotInParallel("AvaloniaSession")].
public class RailSessionViewModelTests {
    static readonly IObservable<IReadOnlySet<string>> NoPending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
    static readonly RepoIdentity Repo = new("path:/repo", "repo");

    static AgentRow Row(
            string id = "a1", string kind = "agent", string vendor = "claude", string status = "Running",
            string? model = "Opus 5", string? title = "Fix the flaky test") =>
        AgentRow.FromLocal(new(id, kind, vendor, "/repo", status, null, null, null, DateTime.UtcNow, model, null, Title: title), Repo);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Title_is_primary_with_vendor_model_age_sub() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(), new BehaviorSubject<string?>(null), NoPending, _ => { }, _ => { });
            await Assert.That(row.Primary).IsEqualTo("Fix the flaky test");
            await Assert.That(row.Sub).StartsWith("claude · Opus 5 · ");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_title_promotes_vendor_and_drops_it_from_sub() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(title: null), new BehaviorSubject<string?>(null), NoPending, _ => { }, _ => { });
            await Assert.That(row.Primary).IsEqualTo("claude");
            await Assert.That(row.Sub).StartsWith("Opus 5 · ");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Review_kind_is_appended_to_the_vendor() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(kind: "review", title: null), new BehaviorSubject<string?>(null), NoPending, _ => { }, _ => { });
            await Assert.That(row.Primary).IsEqualTo("claude · review");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Borrowed_work_location_marks_the_vendor_line_and_the_tooltip_names_the_checkout() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var dto = new AgentStatusDto("a1", "review-flow", "codex", "/repo", "Running", null, null, null, DateTime.UtcNow, "Opus 5", null, Title: "Fix the flaky test") with {
                WorktreePath = "/repo/.capacitor/worktrees/agent-1", WorkLocation = "borrowed",
                BorrowedFrom = "/repo/.capacitor/worktrees/agent-1" };
            using var row = new RailSessionViewModel(AgentRow.FromLocal(dto, Repo), new BehaviorSubject<string?>(null), NoPending, _ => { }, _ => { });
            await Assert.That(row.Sub).StartsWith("codex · review-flow · borrowed · Opus 5 · ");
            await Assert.That(row.Tooltip).Contains("/repo/.capacitor/worktrees/agent-1");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_model_is_omitted_from_sub() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(model: null), new BehaviorSubject<string?>(null), NoPending, _ => { }, _ => { });
            await Assert.That(row.Sub).DoesNotContain("· ·");
            await Assert.That(row.Sub).DoesNotStartWith("·");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Failed_status_sets_the_pip() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var ok = new RailSessionViewModel(Row(), new BehaviorSubject<string?>(null), NoPending, _ => { }, _ => { });
            using var bad = new RailSessionViewModel(Row(status: "Failed"), new BehaviorSubject<string?>(null), NoPending, _ => { }, _ => { });
            await Assert.That(ok.NeedsYou).IsFalse();
            await Assert.That(bad.NeedsYou).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task IsSelected_tracks_the_selection_observable() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var selected = new BehaviorSubject<string?>(null);
            using var row = new RailSessionViewModel(Row(id: "a1"), selected, NoPending, _ => { }, _ => { });
            await Assert.That(row.IsSelected).IsFalse();
            selected.OnNext("a1");
            await Assert.That(row.IsSelected).IsTrue();
            selected.OnNext("other");
            await Assert.That(row.IsSelected).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenCommand_invokes_the_local_callback_with_the_id() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            string? opened = null;
            using var row = new RailSessionViewModel(Row(id: "a9"), new BehaviorSubject<string?>(null), NoPending, id => opened = id, _ => { });
            row.OpenCommand.Execute().Subscribe();
            await Assert.That(opened).IsEqualTo("a9");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Remote_row_is_read_only_and_carries_its_machine_badge() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var dto = new AgentInstanceDto {
                AgentId = "b1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RepoOwner = "o", RepoName = "r",
            };
            string? openedRemote = null;
            using var row = new RailSessionViewModel(
                AgentRow.FromRemote(dto), new BehaviorSubject<string?>(null), NoPending, _ => { }, id => openedRemote = id);
            await Assert.That(row.IsRemote).IsTrue();
            await Assert.That(row.MachineBadge).IsEqualTo("work-mac");
            row.OpenCommand.Execute().Subscribe();
            await Assert.That(openedRemote).IsEqualTo("b1");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Needs_you_follows_the_pending_set_and_the_status() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var pending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            using var row = new RailSessionViewModel(Row(status: "Running"), new BehaviorSubject<string?>(null), pending, _ => { }, _ => { });
            await Assert.That(row.NeedsYou).IsFalse();
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(row.NeedsYou).IsTrue();
            pending.OnNext(new HashSet<string>());
            await Assert.That(row.NeedsYou).IsFalse();

            using var failed = new RailSessionViewModel(Row(status: "Failed"), new BehaviorSubject<string?>(null), pending, _ => { }, _ => { });
            await Assert.That(failed.NeedsYou).IsTrue();
        });
    }
}
