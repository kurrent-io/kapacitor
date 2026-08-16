using ReactiveUnit = System.Reactive.Unit;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views.Onboarding;
using ReactiveUI;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

sealed class FakeWizardStep(WizardStepId id, string? title = null) : IWizardStep {
    public WizardStepId Id { get; } = id;
    public string Title { get; } = title ?? id.ToString();
    public bool Applicable { get; init; } = true;
    public bool Satisfied { get; set; }
    public int EnterCount { get; private set; }
    public Func<WizardNavigation, CancellationToken, Task<bool>>? CanLeave { get; init; }

    public Task OnEnterAsync(CancellationToken ct) {
        EnterCount++;
        return Task.CompletedTask;
    }

    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) =>
        CanLeave?.Invoke(direction, ct) ?? Task.FromResult(true);
}

/// Dispatches through the real headless session (ConsentPromptViewModelTests' pattern) — the
/// constructor's WhenAnyValue call trips ReactiveUI's init guard under WithImmediateRxScheduler.
public class OnboardingViewModelTests {
    static bool CanExecute(ReactiveCommand<ReactiveUnit, ReactiveUnit> command) {
        var value = false;
        using var sub = command.CanExecute.Subscribe(v => value = v);
        return value;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Steps_excludes_non_applicable_entries_and_starts_on_the_first_applicable_one() {
        var (stepIds, currentId) = await AvaloniaSession.DispatchAsync(async () => {
            var shim = new FakeWizardStep(WizardStepId.Shim) { Applicable = false };
            var connect = new FakeWizardStep(WizardStepId.Connect);
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([shim, connect, done]);
            await vm.PendingEnterForTesting;

            return (vm.Steps.Select(s => s.Id).ToList(), vm.Current.Id);
        });

        await Assert.That(stepIds).IsEquivalentTo([WizardStepId.Connect, WizardStepId.Done], CollectionOrdering.Matching);
        await Assert.That(currentId).IsEqualTo(WizardStepId.Connect);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_back_and_skip_move_through_the_full_step_order() {
        var (afterNext, afterSkip, afterBack1, afterBack2) = await AvaloniaSession.DispatchAsync(async () => {
            var steps = new[] {
                new FakeWizardStep(WizardStepId.Connect),
                new FakeWizardStep(WizardStepId.SignIn),
                new FakeWizardStep(WizardStepId.Done),
            };
            var vm = new OnboardingViewModel(steps);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask();
            var afterNext = vm.Current.Id;

            await vm.SkipCommand.Execute().ToTask();
            var afterSkip = vm.Current.Id;

            await vm.BackCommand.Execute().ToTask();
            var afterBack1 = vm.Current.Id;

            await vm.BackCommand.Execute().ToTask();
            var afterBack2 = vm.Current.Id;

            return (afterNext, afterSkip, afterBack1, afterBack2);
        });

        await Assert.That(afterNext).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(afterSkip).IsEqualTo(WizardStepId.Done);
        await Assert.That(afterBack1).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(afterBack2).IsEqualTo(WizardStepId.Connect);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task CanLeaveAsync_false_holds_the_current_step_for_every_direction() {
        var (afterNext, afterVetoedNext, afterVetoedSkip, afterVetoedBack) = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Connect);
            var signIn = new FakeWizardStep(WizardStepId.SignIn) { CanLeave = (_, _) => Task.FromResult(false) };
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([connect, signIn, done]);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask(); // Connect -> SignIn, allowed
            var afterNext = vm.Current.Id;

            await vm.NextCommand.Execute().ToTask(); // vetoed
            var afterVetoedNext = vm.Current.Id;

            await vm.SkipCommand.Execute().ToTask(); // vetoed
            var afterVetoedSkip = vm.Current.Id;

            await vm.BackCommand.Execute().ToTask(); // vetoed
            var afterVetoedBack = vm.Current.Id;

            return (afterNext, afterVetoedNext, afterVetoedSkip, afterVetoedBack);
        });

        await Assert.That(afterNext).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(afterVetoedNext).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(afterVetoedSkip).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(afterVetoedBack).IsEqualTo(WizardStepId.SignIn);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Skip_then_return_leaves_the_step_unsatisfied() {
        var (afterSkip, afterBack, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Connect);
            var signIn = new FakeWizardStep(WizardStepId.SignIn);
            var vm = new OnboardingViewModel([connect, signIn]);
            await vm.PendingEnterForTesting;

            await vm.SkipCommand.Execute().ToTask();
            var afterSkip = vm.Current.Id;

            await vm.BackCommand.Execute().ToTask();
            var afterBack = vm.Current.Id;

            return (afterSkip, afterBack, vm.Current.Satisfied);
        });

        await Assert.That(afterSkip).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(afterBack).IsEqualTo(WizardStepId.Connect);
        await Assert.That(satisfied).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OnEnterAsync_fires_on_initial_entry_and_again_on_re_entry() {
        var (connectEntersInitially, signInEntersInitially, signInEntersAfterNext, connectEntersAfterBack) =
            await AvaloniaSession.DispatchAsync(async () => {
                var connect = new FakeWizardStep(WizardStepId.Connect);
                var signIn = new FakeWizardStep(WizardStepId.SignIn);
                var vm = new OnboardingViewModel([connect, signIn]);
                await vm.PendingEnterForTesting;

                var connectEntersInitially = connect.EnterCount;
                var signInEntersInitially = signIn.EnterCount;

                await vm.NextCommand.Execute().ToTask();
                var signInEntersAfterNext = signIn.EnterCount;

                await vm.BackCommand.Execute().ToTask();
                var connectEntersAfterBack = connect.EnterCount;

                return (connectEntersInitially, signInEntersInitially, signInEntersAfterNext, connectEntersAfterBack);
            });

        await Assert.That(connectEntersInitially).IsEqualTo(1);
        await Assert.That(signInEntersInitially).IsEqualTo(0);
        await Assert.That(signInEntersAfterNext).IsEqualTo(1);
        await Assert.That(connectEntersAfterBack).IsEqualTo(2); // re-entry
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Back_is_disabled_on_the_first_step_and_skip_is_disabled_on_the_last_step() {
        var (backOnFirst, skipOnFirst, backOnLast, skipOnLast) = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Connect);
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([connect, done]);
            await vm.PendingEnterForTesting;

            var backOnFirst = CanExecute(vm.BackCommand);
            var skipOnFirst = CanExecute(vm.SkipCommand);

            await vm.NextCommand.Execute().ToTask(); // -> Done

            var backOnLast = CanExecute(vm.BackCommand);
            var skipOnLast = CanExecute(vm.SkipCommand);

            return (backOnFirst, skipOnFirst, backOnLast, skipOnLast);
        });

        await Assert.That(backOnFirst).IsFalse();
        await Assert.That(skipOnFirst).IsTrue();
        await Assert.That(backOnLast).IsTrue();
        await Assert.That(skipOnLast).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_on_the_last_step_finishes_and_raises_CloseRequested_exactly_once() {
        var (finalStepId, closeCount) = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Connect);
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([connect, done]);
            await vm.PendingEnterForTesting;

            var count = 0;
            vm.CloseRequested += () => count++;

            await vm.NextCommand.Execute().ToTask(); // Connect -> Done
            await vm.NextCommand.Execute().ToTask(); // finish

            return (vm.Current.Id, count);
        });

        await Assert.That(finalStepId).IsEqualTo(WizardStepId.Done);
        await Assert.That(closeCount).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_close_raises_CloseRequested() {
        var closeCount = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Connect);
            var vm = new OnboardingViewModel([connect]);
            await vm.PendingEnterForTesting;

            var count = 0;
            vm.CloseRequested += () => count++;

            var window = new OnboardingWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return count;
        });

        await Assert.That(closeCount).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_renders_the_current_steps_title() {
        var rendered = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Connect, "Connect to Capacitor");
            var vm = new OnboardingViewModel([connect]);
            await vm.PendingEnterForTesting;

            var window = new OnboardingWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var text = window.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "StepTitleText")?.Text;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return text;
        });

        await Assert.That(rendered).IsEqualTo("Connect to Capacitor");
    }
}
