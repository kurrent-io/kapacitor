using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.ConsentEntries;

namespace Capacitor.App.Tests.Unit;

/// The window-lifetime half of spec §6: at most one prompt window, raised only when it is not
/// already visible, and always from the UI thread — the entry-added signal originates on a socket
/// continuation. Real headless ConsentPromptWindows over a real ConsentPromptViewModel, so this
/// also exercises the production composition App's factory builds.
public class ConsentPromptCoordinatorTests {
    sealed class Fixture : IDisposable {
        public readonly FakeConsentService Consent = new();
        public readonly AppNotifier Notifier = new();
        public readonly FakeTicker Ticker = new();
        public readonly List<ConsentPromptWindow> Windows = [];
        public readonly ConsentPromptCoordinator Coordinator;

        public Fixture() {
            Coordinator = new ConsentPromptCoordinator(Consent, () => {
                var window = new ConsentPromptWindow {
                    DataContext = new ConsentPromptViewModel(
                        Consent, Notifier, Ticker, new FakeTimeProvider(T0), CancellationToken.None),
                    Notifier = Notifier,
                };
                Windows.Add(window);
                return window;
            });
        }

        public ConsentPromptWindow Last => Windows[^1];

        public void Dispose() {
            Coordinator.Dispose();
            Dispatcher.UIThread.RunJobs();
            Consent.Dispose();
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Raise_on_entry_added_while_window_not_visible_marshals_to_ui_thread() {
        var (builds, visible, raises, buildsAfterSecond, raisesAfterSecond, stillVisible) =
            await AvaloniaSession.DispatchAsync(async () => {
                using var f = new Fixture();

                // The real signal arrives on a socket continuation, never the UI thread.
                await Task.Run(() => f.Consent.Add(Entry("a1", "p1")));
                Dispatcher.UIThread.RunJobs();

                var first = (f.Windows.Count, f.Last.IsVisible, f.Coordinator.Raises);

                await Task.Run(() => f.Consent.Add(Entry("a2", "p2", requestedAt: T0.AddSeconds(5))));
                Dispatcher.UIThread.RunJobs();

                return (first.Count, first.IsVisible, first.Raises,
                        f.Windows.Count, f.Coordinator.Raises, f.Last.IsVisible);
            });

        await Assert.That(builds).IsEqualTo(1);
        await Assert.That(visible).IsTrue();
        await Assert.That(raises).IsEqualTo(1);
        // Already visible: no second window and no re-activation — never steal focus mid-decision.
        await Assert.That(buildsAfterSecond).IsEqualTo(1);
        await Assert.That(raisesAfterSecond).IsEqualTo(1);
        await Assert.That(stillVisible).IsTrue();
    }

    /// Closing without deciding is an explicit defer: the queue is untouched and the tray keeps
    /// its attention state. A later ShowPromptWindow (the tray menu item) builds a fresh window —
    /// Avalonia refuses to Show() a closed one — over the same, still-pending queue.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Close_is_defer_reopen_via_show() {
        var (pendingAfterClose, resolves, builds, differentInstance, visible, reopenedRequestId) =
            await AvaloniaSession.DispatchAsync(() => {
                using var f = new Fixture();
                f.Consent.Add(Entry("a1", "p1"));
                f.Coordinator.ShowPromptWindow();
                Dispatcher.UIThread.RunJobs();
                var deferred = f.Last;

                deferred.Close();
                Dispatcher.UIThread.RunJobs();

                var cacheCount = f.Consent.Cache.Count;
                var resolveCount = f.Consent.Resolved.Count;

                f.Coordinator.ShowPromptWindow();
                Dispatcher.UIThread.RunJobs();

                var reopened = f.Last;
                var vm = (ConsentPromptViewModel)reopened.DataContext!;
                return (cacheCount, resolveCount, f.Windows.Count, !ReferenceEquals(deferred, reopened),
                        reopened.IsVisible, vm.Current?.RequestId);
            });

        await Assert.That(pendingAfterClose).IsEqualTo(1); // a defer decides nothing
        await Assert.That(resolves).IsEqualTo(0);
        await Assert.That(builds).IsEqualTo(2);
        await Assert.That(differentInstance).IsTrue();
        await Assert.That(visible).IsTrue();
        await Assert.That(reopenedRequestId).IsEqualTo("a1"); // the same queue, still pending
    }

    /// The window's own close: an advance that finds nothing left (spec §6). Proves the
    /// ViewModel→window wiring, and that the coordinator releases the instance so the next
    /// arrival raises a fresh one rather than trying to Show() a closed window.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_closes_itself_when_the_queue_empties() {
        var (visibleAfterDecision, builds, reopenedVisible) = await AvaloniaSession.DispatchAsync(async () => {
            using var f = new Fixture();
            f.Consent.Add(Entry("a1", "p1"));
            f.Coordinator.ShowPromptWindow();
            Dispatcher.UIThread.RunJobs();
            var window = f.Last;
            var vm = (ConsentPromptViewModel)window.DataContext!;

            f.Consent.Queue(ConsentResolveKind.Applied, ConsentRuleOutcome.NotRequested);
            await vm.AllowOnceCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();

            var closed = window.IsVisible;

            await Task.Run(() => f.Consent.Add(Entry("a2", "p2", requestedAt: T0.AddSeconds(5))));
            Dispatcher.UIThread.RunJobs();

            return (closed, f.Windows.Count, f.Last.IsVisible);
        });

        await Assert.That(visibleAfterDecision).IsFalse();
        await Assert.That(builds).IsEqualTo(2);
        await Assert.That(reopenedVisible).IsTrue();
    }

    /// Regression coverage for an Important defect found in review: on the LAST pending request —
    /// the common single-prompt case — the rule-not-saved warning was notified and then thrown
    /// away, because the advance emptied the queue and closed the window on the same beat, before
    /// the posted toast ever rendered. Exactly the disclosure spec §6's "never a silent success"
    /// exists for. Asserted on what the user can observe: the window still up, with the warning on
    /// screen.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rule_warning_on_the_last_pending_request_is_actually_shown() {
        var (visibleAfterAck, rendered, visibleAfterHold, builds) = await AvaloniaSession.DispatchAsync(async () => {
            using var f = new Fixture();
            f.Consent.Add(Entry("a1", "p1")); // the ONLY pending request
            f.Coordinator.ShowPromptWindow();
            Dispatcher.UIThread.RunJobs();
            var window = f.Last;
            var vm = (ConsentPromptViewModel)window.DataContext!;

            f.Consent.Queue(ConsentResolveKind.AppliedRuleRejected, ConsentRuleOutcome.Rejected, "store full");
            await vm.AllowRememberCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();

            var visible = window.IsVisible;
            var texts = string.Join('\n', window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));

            f.Ticker.Tick();
            f.Ticker.Tick();
            Dispatcher.UIThread.RunJobs();

            return (visible, texts, window.IsVisible, f.Windows.Count);
        });

        await Assert.That(visibleAfterAck).IsTrue();
        await Assert.That(rendered).Contains("Decision applied — rule not saved: store full");
        await Assert.That(visibleAfterHold).IsFalse(); // disclosed for the hold, then closed
        await Assert.That(builds).IsEqualTo(1);
    }

    /// Rendering acceptance for the §6 copy: the bound text actually reaches the screen (a
    /// mistyped binding path renders empty), including the toast overlay this window owns because
    /// the main window may be closed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_renders_the_request_the_countdown_and_the_three_buttons() {
        var (texts, buttons, tooltip) = await AvaloniaSession.DispatchAsync(() => {
            using var f = new Fixture();
            f.Consent.Add(Entry("a1", "p1", kind: "review-flow", repoPath: "/repos/kcap-cli"));
            f.Consent.Add(Entry("a2", "p2", requestedAt: T0.AddSeconds(5)));
            f.Coordinator.ShowPromptWindow();
            Dispatcher.UIThread.RunJobs();

            f.Notifier.Notify("Daemon unreachable — the request is still pending");
            Dispatcher.UIThread.RunJobs();

            var rendered = string.Join('\n', f.Last.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));
            var labels = f.Last.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToArray();
            var remember = f.Last.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Allow & remember"));
            return (rendered, labels, ToolTip.GetTip(remember) as string);
        });

        await Assert.That(texts).Contains("Alice");
        await Assert.That(texts).Contains("Review flow");
        await Assert.That(texts).Contains("claude");
        await Assert.That(texts).Contains("kcap-cli");
        await Assert.That(texts).Contains("Expires in 30s");
        await Assert.That(texts).Contains("1 of 2");
        await Assert.That(texts).Contains("Daemon unreachable — the request is still pending"); // the toast overlay
        await Assert.That(buttons).Contains("Allow once");
        await Assert.That(buttons).Contains("Allow & remember");
        await Assert.That(buttons).Contains("Deny");
        await Assert.That(tooltip).IsEqualTo(
            "Saves a rule allowing future launches from this requester. Existing deny rules — including Pause — take precedence until removed.");
    }

    /// Shutdown (spec §5): the coordinator is disposed BEFORE ConsentService, so the window — and
    /// any resolve it has in flight — is gone before the service it would call into.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispose_closes_the_window() {
        var (visibleBefore, visibleAfter, windows, raisesAfterDispose) = await AvaloniaSession.DispatchAsync(() => {
            using var f = new Fixture();
            f.Consent.Add(Entry("a1", "p1"));
            f.Coordinator.ShowPromptWindow();
            Dispatcher.UIThread.RunJobs();

            var before = f.Last.IsVisible;
            f.Coordinator.Dispose();
            Dispatcher.UIThread.RunJobs();
            var after = f.Last.IsVisible;

            // A signal arriving after disposal must not resurrect a window.
            f.Consent.Add(Entry("a2", "p2", requestedAt: T0.AddSeconds(5)));
            Dispatcher.UIThread.RunJobs();

            return (before, after, f.Windows.Count, f.Coordinator.Raises);
        });

        await Assert.That(visibleBefore).IsTrue();
        await Assert.That(visibleAfter).IsFalse();
        await Assert.That(windows).IsEqualTo(1);
        await Assert.That(raisesAfterDispose).IsEqualTo(1);
    }
}
