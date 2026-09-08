using ReactiveUnit = System.Reactive.Unit;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views.Onboarding;
using ReactiveUI.Reactive;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

sealed class FakeWizardStep(WizardStepId id, string? title = null) : IWizardStep {
    public WizardStepId Id { get; } = id;
    public string Title { get; } = title ?? id.ToString();
    public bool Applicable { get; init; } = true;
    public bool Satisfied { get; set; }
    public int EnterCount { get; private set; }
    public bool ThrowOnEnter { get; init; }
    public Func<WizardNavigation, CancellationToken, Task<bool>>? CanLeave { get; init; }

    public Task OnEnterAsync(CancellationToken ct) {
        EnterCount++;
        if (ThrowOnEnter) throw new InvalidOperationException("boom: enter failed");
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

    // ── Busy gate and veto handling ──────────────────────────

    /// Skip suspends on a pending veto check; a directly-invoked Next (bypassing
    /// the canExecute-driven Button disable a real UI enforces) must be a silent no-op — the
    /// shared busy gate inside NavigateAsync itself, not just canExecute — rather than racing
    /// Skip's own _index mutation once the veto later resolves.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Skip_blocked_on_a_pending_veto_cannot_race_a_concurrent_Next() {
        var currentId = await AvaloniaSession.DispatchAsync(async () => {
            var gate = new TaskCompletionSource<bool>();
            var connect = new FakeWizardStep(WizardStepId.Connect) {
                CanLeave = (direction, _) => direction == WizardNavigation.Skip ? gate.Task : Task.FromResult(true),
            };
            var signIn = new FakeWizardStep(WizardStepId.SignIn);
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([connect, signIn, done]);
            await vm.PendingEnterForTesting;

            var skipTask = vm.SkipCommand.Execute().ToTask(); // starts, suspends on gate.Task
            await vm.NextCommand.Execute().ToTask(); // must be a silent no-op — no exception

            gate.SetResult(true); // release Skip's veto check
            await skipTask;

            return vm.Current.Id;
        });

        await Assert.That(currentId).IsEqualTo(WizardStepId.SignIn); // exactly one transition, no double-increment
    }

    /// The busy gate also drives canExecute itself — a real bound Button disables for all three
    /// directions the instant one navigation starts, not just the internal early-return above.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task All_three_commands_disable_while_one_navigation_is_in_flight() {
        var (backWhileBusy, skipWhileBusy, nextWhileBusy, backAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var gate = new TaskCompletionSource<bool>();
            var connect = new FakeWizardStep(WizardStepId.Connect) { CanLeave = (_, _) => gate.Task };
            var signIn = new FakeWizardStep(WizardStepId.SignIn);
            var vm = new OnboardingViewModel([connect, signIn]);
            await vm.PendingEnterForTesting;

            var skipTask = vm.SkipCommand.Execute().ToTask(); // starts, suspends on gate.Task

            var backWhileBusy = CanExecute(vm.BackCommand);
            var skipWhileBusy = CanExecute(vm.SkipCommand);
            var nextWhileBusy = CanExecute(vm.NextCommand);

            gate.SetResult(true);
            await skipTask;

            return (backWhileBusy, skipWhileBusy, nextWhileBusy, CanExecute(vm.BackCommand));
        });

        await Assert.That(backWhileBusy).IsFalse();
        await Assert.That(skipWhileBusy).IsFalse();
        await Assert.That(nextWhileBusy).IsFalse();
        await Assert.That(backAfter).IsTrue(); // idle again once the transition settles
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task CanLeaveAsync_throwing_is_treated_as_a_veto_and_does_not_crash() {
        var currentId = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Connect) { CanLeave = (_, _) => throw new InvalidOperationException("boom") };
            var signIn = new FakeWizardStep(WizardStepId.SignIn);
            var vm = new OnboardingViewModel([connect, signIn]);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask(); // must not throw out of the command

            return vm.Current.Id;
        });

        await Assert.That(currentId).IsEqualTo(WizardStepId.Connect); // treated as a veto: stayed
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OnEnterAsync_throwing_still_completes_the_transition_and_does_not_crash() {
        var currentId = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Connect);
            var signIn = new FakeWizardStep(WizardStepId.SignIn) { ThrowOnEnter = true };
            var vm = new OnboardingViewModel([connect, signIn]);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask(); // must not throw out of the command

            return vm.Current.Id;
        });

        await Assert.That(currentId).IsEqualTo(WizardStepId.SignIn); // transition still landed
    }

    // ── TryGoTo: the Sign-in step's retarget jump ────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task TryGoTo_jumps_backwards_and_enters_the_target_step() {
        var (accepted, currentId, entered) = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Connect);
            var signIn = new FakeWizardStep(WizardStepId.SignIn);
            var vm = new OnboardingViewModel([connect, signIn]);
            await vm.PendingEnterForTesting;
            await vm.NextCommand.Execute().ToTask(); // now on Sign-in

            var ok = vm.TryGoTo(WizardStepId.Connect);
            await WaitForIdleAsync(vm);

            return (ok, vm.Current.Id, connect.EnterCount);
        });

        await Assert.That(accepted).IsTrue();
        await Assert.That(currentId).IsEqualTo(WizardStepId.Connect);
        await Assert.That(entered).IsEqualTo(2); // the initial entry plus the jump
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task TryGoTo_consults_the_current_steps_veto_like_any_transition() {
        var (accepted, currentId) = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Connect);
            var signIn = new FakeWizardStep(WizardStepId.SignIn) { CanLeave = (_, _) => Task.FromResult(false) };
            var vm = new OnboardingViewModel([connect, signIn]);
            await vm.PendingEnterForTesting;
            await vm.NextCommand.Execute().ToTask(); // now on Sign-in, which refuses to leave

            var ok = vm.TryGoTo(WizardStepId.Connect);
            await WaitForIdleAsync(vm);

            return (ok, vm.Current.Id);
        });

        await Assert.That(accepted).IsTrue();  // the jump was admitted...
        await Assert.That(currentId).IsEqualTo(WizardStepId.SignIn); // ...and then vetoed by the step
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task TryGoTo_is_refused_while_another_navigation_is_in_flight() {
        var (secondAttempt, currentId) = await AvaloniaSession.DispatchAsync(async () => {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connect = new FakeWizardStep(WizardStepId.Connect) { CanLeave = (_, _) => gate.Task };
            var signIn = new FakeWizardStep(WizardStepId.SignIn);
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([connect, signIn, done]);
            await vm.PendingEnterForTesting;

            var first = vm.NextCommand.Execute().ToTask(); // parks inside Connect's CanLeaveAsync
            var refused = vm.TryGoTo(WizardStepId.Done);

            gate.SetResult(true);
            await first;
            await WaitForIdleAsync(vm);

            return (refused, vm.Current.Id);
        }).WaitAsync(TimeSpan.FromSeconds(20));

        await Assert.That(secondAttempt).IsFalse();
        await Assert.That(currentId).IsEqualTo(WizardStepId.SignIn); // only the first navigation landed
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task TryGoTo_refuses_a_step_that_is_not_part_of_this_run() {
        var accepted = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Connect);
            var shim = new FakeWizardStep(WizardStepId.Shim) { Applicable = false };
            var vm = new OnboardingViewModel([shim, connect]);
            await vm.PendingEnterForTesting;

            return vm.TryGoTo(WizardStepId.Shim);
        });

        await Assert.That(accepted).IsFalse();
    }

    // TryGoTo's navigation is fire-and-forget by design (it answers the caller immediately), so
    // tests wait for the shared gate to reopen rather than for a task they were never handed.
    static async Task WaitForIdleAsync(OnboardingViewModel vm) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!CanExecute(vm.NextCommand)) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the wizard never went idle");
            await Task.Delay(10);
        }
    }
}
