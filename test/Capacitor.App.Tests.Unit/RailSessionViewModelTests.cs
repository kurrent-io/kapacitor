using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// RailSessionViewModel's OpenCommand and IsSelected OAPH both run through
/// RxSchedulers.MainThreadScheduler, which is not immediate in a bare test process — see
/// MainWindowViewModelTests' header comment. Every test here runs inside
/// AvaloniaSession.WithImmediateRxScheduler and carries [NotInParallel("AvaloniaSession")].
public class RailSessionViewModelTests {
    static AgentStatusDto Dto(
            string id = "a1", string kind = "agent", string vendor = "claude", string status = "Running",
            string? model = "Opus 5", string? title = "Fix the flaky test") =>
        new(id, kind, vendor, "/repo", status, null, null, null, DateTime.UtcNow, model, null, Title: title);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Title_is_primary_with_vendor_model_age_sub() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Dto(), new BehaviorSubject<string?>(null), _ => { });
            await Assert.That(row.Primary).IsEqualTo("Fix the flaky test");
            await Assert.That(row.Sub).StartsWith("claude · Opus 5 · ");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_title_promotes_vendor_and_drops_it_from_sub() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Dto(title: null), new BehaviorSubject<string?>(null), _ => { });
            await Assert.That(row.Primary).IsEqualTo("claude");
            await Assert.That(row.Sub).StartsWith("Opus 5 · ");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Review_kind_is_appended_to_the_vendor() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Dto(kind: "review", title: null), new BehaviorSubject<string?>(null), _ => { });
            await Assert.That(row.Primary).IsEqualTo("claude · review");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_model_is_omitted_from_sub() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Dto(model: null), new BehaviorSubject<string?>(null), _ => { });
            await Assert.That(row.Sub).DoesNotContain("· ·");
            await Assert.That(row.Sub).DoesNotStartWith("·");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Failed_status_sets_the_pip() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var ok = new RailSessionViewModel(Dto(), new BehaviorSubject<string?>(null), _ => { });
            using var bad = new RailSessionViewModel(Dto(status: "Failed"), new BehaviorSubject<string?>(null), _ => { });
            await Assert.That(ok.NeedsYou).IsFalse();
            await Assert.That(bad.NeedsYou).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task IsSelected_tracks_the_selection_observable() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var selected = new BehaviorSubject<string?>(null);
            using var row = new RailSessionViewModel(Dto(id: "a1"), selected, _ => { });
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
            using var row = new RailSessionViewModel(Dto(id: "a9"), new BehaviorSubject<string?>(null), id => opened = id);
            row.OpenCommand.Execute().Subscribe();
            await Assert.That(opened).IsEqualTo("a9");
        });
    }
}
