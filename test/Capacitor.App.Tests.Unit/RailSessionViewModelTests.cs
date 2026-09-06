using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// RailSessionViewModel's OpenCommand and IsSelected OAPH both run through
/// RxSchedulers.MainThreadScheduler, which is not immediate in a bare test process — see
/// MainWindowViewModelTests' header comment. Every test here runs inside
/// AvaloniaSession.WithImmediateRxScheduler and carries [NotInParallel("AvaloniaSession")].
public class RailSessionViewModelTests {
    static readonly IObservable<IReadOnlySet<string>> NoPending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());

    static AgentStatusDto Dto(
            string id = "a1", string kind = "agent", string vendor = "claude", string status = "Running",
            string? model = "Opus 5", string? title = "Fix the flaky test") =>
        new(id, kind, vendor, "/repo", status, null, null, null, DateTime.UtcNow, model, null, Title: title);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Title_is_primary_with_vendor_model_age_sub() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Dto(), new BehaviorSubject<string?>(null), NoPending, _ => { });
            await Assert.That(row.Primary).IsEqualTo("Fix the flaky test");
            await Assert.That(row.Sub).StartsWith("claude · Opus 5 · ");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_title_promotes_vendor_and_drops_it_from_sub() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Dto(title: null), new BehaviorSubject<string?>(null), NoPending, _ => { });
            await Assert.That(row.Primary).IsEqualTo("claude");
            await Assert.That(row.Sub).StartsWith("Opus 5 · ");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Review_kind_is_appended_to_the_vendor() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Dto(kind: "review", title: null), new BehaviorSubject<string?>(null), NoPending, _ => { });
            await Assert.That(row.Primary).IsEqualTo("claude · review");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Borrowed_work_location_marks_the_vendor_line_and_the_tooltip_names_the_checkout() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var dto = Dto(kind: "review-flow", vendor: "codex") with {
                WorktreePath = "/repo/.capacitor/worktrees/agent-1", WorkLocation = "borrowed",
                BorrowedFrom = "/repo/.capacitor/worktrees/agent-1" };
            using var row = new RailSessionViewModel(dto, new BehaviorSubject<string?>(null), NoPending, _ => { });
            await Assert.That(row.Sub).StartsWith("codex · review-flow · borrowed · Opus 5 · ");
            await Assert.That(row.Tooltip).Contains("/repo/.capacitor/worktrees/agent-1");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_model_is_omitted_from_sub() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Dto(model: null), new BehaviorSubject<string?>(null), NoPending, _ => { });
            await Assert.That(row.Sub).DoesNotContain("· ·");
            await Assert.That(row.Sub).DoesNotStartWith("·");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Failed_status_sets_the_pip() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var ok = new RailSessionViewModel(Dto(), new BehaviorSubject<string?>(null), NoPending, _ => { });
            using var bad = new RailSessionViewModel(Dto(status: "Failed"), new BehaviorSubject<string?>(null), NoPending, _ => { });
            await Assert.That(ok.NeedsYou).IsFalse();
            await Assert.That(bad.NeedsYou).IsTrue();
        });
    }

    /// The daemon's own verdict that the agent finished its turn lights the same pip a pending
    /// ask does, and the tooltip says which it is.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Awaiting_input_sets_the_pip_and_names_it_in_the_tooltip() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var waiting = new RailSessionViewModel(Dto() with { AwaitingInput = true }, new BehaviorSubject<string?>(null), NoPending, _ => { });
            using var working = new RailSessionViewModel(Dto() with { AwaitingInput = false }, new BehaviorSubject<string?>(null), NoPending, _ => { });
            using var older   = new RailSessionViewModel(Dto() with { AwaitingInput = null }, new BehaviorSubject<string?>(null), NoPending, _ => { });
            await Assert.That(waiting.NeedsYou).IsTrue();
            await Assert.That(waiting.Tooltip).Contains("waiting for input");
            await Assert.That(working.NeedsYou).IsFalse();
            await Assert.That(working.Tooltip).DoesNotContain("waiting for input");
            await Assert.That(older.NeedsYou).IsFalse();
        });
    }

    /// A flow participant between rounds waits on the flow, not on the user, who cannot message
    /// it anyway.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Awaiting_input_on_a_flow_participant_does_not_set_the_pip() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Dto(kind: "review-flow") with { AwaitingInput = true }, new BehaviorSubject<string?>(null), NoPending, _ => { });
            await Assert.That(row.NeedsYou).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task IsSelected_tracks_the_selection_observable() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var selected = new BehaviorSubject<string?>(null);
            using var row = new RailSessionViewModel(Dto(id: "a1"), selected, NoPending, _ => { });
            await Assert.That(row.IsSelected).IsFalse();
            selected.OnNext("a1");
            await Assert.That(row.IsSelected).IsTrue();
            selected.OnNext("other");
            await Assert.That(row.IsSelected).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenCommand_invokes_the_callback_with_the_id() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            string? opened = null;
            using var row = new RailSessionViewModel(Dto(id: "a9"), new BehaviorSubject<string?>(null), NoPending, id => opened = id);
            row.OpenCommand.Execute().Subscribe();
            await Assert.That(opened).IsEqualTo("a9");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Needs_you_follows_the_pending_set_and_the_status() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var pending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            using var row = new RailSessionViewModel(Dto(status: "Running"), new BehaviorSubject<string?>(null), pending, _ => { });
            await Assert.That(row.NeedsYou).IsFalse();
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(row.NeedsYou).IsTrue();
            pending.OnNext(new HashSet<string>());
            await Assert.That(row.NeedsYou).IsFalse();

            using var failed = new RailSessionViewModel(Dto(status: "Failed"), new BehaviorSubject<string?>(null), pending, _ => { });
            await Assert.That(failed.NeedsYou).IsTrue();
        });
    }
}
