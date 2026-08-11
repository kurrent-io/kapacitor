using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// LifecyclePromptViewModel has no activation-scoped state (no queue, no ticker — unlike
/// ConsentPromptViewModel, which this suite is modeled on) so these run under
/// AvaloniaSession.WithImmediateRxScheduler — TrayViewModelTests/MainWindowViewModelTests' own
/// pattern for a plain, non-activatable VM — rather than the real headless dispatcher
/// ConsentPromptViewModelTests needs for its Activator/ticker plumbing.
public class LifecyclePromptViewModelTests {
    static LifecyclePrompt Prompt(string kind, bool pathDegraded = false, string disclosure = "the unit will be replaced.") =>
        new(kind, "1.0.0", "1.1.0", pathDegraded, disclosure);

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(LifecyclePrompt.KindRestartUpdate, "Restart daemon to update")]
    [Arguments(LifecyclePrompt.KindTakeover, "Take over daemon management")]
    [Arguments(LifecyclePrompt.KindRepair, "Repair daemon service")]
    public async Task Title_is_kind_specific(string kind, string expectedTitle) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(Prompt(kind), new TaskCompletionSource<bool>());

            await Assert.That(vm.Title).IsEqualTo(expectedTitle);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Disclosure_is_always_present() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(
                Prompt(LifecyclePrompt.KindRepair, disclosure: "the existing unit will be replaced and its settings re-captured."),
                new TaskCompletionSource<bool>());

            await Assert.That(vm.Disclosure).IsEqualTo("the existing unit will be replaced and its settings re-captured.");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PathDegraded_false_renders_no_degraded_path_sentence() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindRestartUpdate, pathDegraded: false), new TaskCompletionSource<bool>());

            await Assert.That(vm.PathDegraded).IsFalse();
            await Assert.That(vm.DegradedPathText).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PathDegraded_true_adds_the_degraded_path_sentence() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindTakeover, pathDegraded: true), new TaskCompletionSource<bool>());

            await Assert.That(vm.PathDegraded).IsTrue();
            await Assert.That(vm.DegradedPathText).IsNotNull();
            await Assert.That(vm.DegradedPathText!).Contains("PATH");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Accept_resolves_the_task_true_and_requests_close() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var tcs = new TaskCompletionSource<bool>();
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindRepair), tcs);
            var closeRequests = 0;
            using var sub = vm.CloseRequested.Subscribe(_ => closeRequests++);

            await vm.AcceptCommand.Execute().ToTask();

            await Assert.That(tcs.Task.IsCompletedSuccessfully).IsTrue();
            await Assert.That(tcs.Task.Result).IsTrue();
            await Assert.That(closeRequests).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Decline_resolves_the_task_false_and_requests_close() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var tcs = new TaskCompletionSource<bool>();
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindRepair), tcs);
            var closeRequests = 0;
            using var sub = vm.CloseRequested.Subscribe(_ => closeRequests++);

            await vm.DeclineCommand.Execute().ToTask();

            await Assert.That(tcs.Task.IsCompletedSuccessfully).IsTrue();
            await Assert.That(tcs.Task.Result).IsFalse();
            await Assert.That(closeRequests).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Accept_after_external_cancellation_is_a_silent_no_op() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var tcs = new TaskCompletionSource<bool>();
            tcs.TrySetResult(false); // simulates WireDialogCancellation already having resolved it
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindRepair), tcs);

            await vm.AcceptCommand.Execute().ToTask();

            await Assert.That(tcs.Task.Result).IsFalse(); // TrySetResult on Accept is a no-op
        });
    }
}
