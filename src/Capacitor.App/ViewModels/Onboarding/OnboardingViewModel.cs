using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services.Onboarding;
using ReactiveUI;

namespace Capacitor.App.ViewModels.Onboarding;

/// Back/Next/Skip all funnel through Current's CanLeaveAsync veto; Next on the last step finishes.
public sealed class OnboardingViewModel : ReactiveObject {
    readonly CancellationToken _shutdownToken;
    bool _closed;
    bool _navigating;

    public IReadOnlyList<IWizardStep> Steps { get; }

    /// Wizard-first mode builds no tray and no main window, so the outcome consumer's Status/
    /// Attention lines are rendered here (spec decision 2). Null in tests that don't need them.
    public WizardLifecycleSurface? Surface { get; }

    int _index;

    IWizardStep _current;
    public IWizardStep Current {
        get => _current;
        private set => this.RaiseAndSetIfChanged(ref _current, value);
    }

    // Shared across Back/Next/Skip: only one of the three may be mid-transition at a time.
    bool Navigating {
        get => _navigating;
        set => this.RaiseAndSetIfChanged(ref _navigating, value);
    }

    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipCommand { get; }

    /// Fires once per logical close: the Done step's finish, or the window closing.
    public event Action? CloseRequested;

    /// The constructor's own initial-entry call, exposed so a test can await it deterministically.
    internal Task PendingEnterForTesting { get; }

    public OnboardingViewModel(
            IEnumerable<IWizardStep> steps, CancellationToken shutdownToken = default,
            WizardLifecycleSurface? surface = null) {
        _shutdownToken = shutdownToken;
        Surface = surface;
        Steps = steps.Where(s => s.Applicable).ToList();
        if (Steps.Count == 0) throw new ArgumentException("at least one applicable step is required", nameof(steps));

        _current = Steps[0];

        var currentChanged = this.WhenAnyValue(x => x.Current);
        var idle = this.WhenAnyValue(x => x.Navigating).Select(busy => !busy);
        var canBack = currentChanged.CombineLatest(idle, (_, notBusy) => notBusy && _index > 0);
        var canSkip = currentChanged.CombineLatest(idle, (_, notBusy) => notBusy && _index < Steps.Count - 1);

        BackCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Back), canBack);
        SkipCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Skip), canSkip);
        NextCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Next), idle);

        PendingEnterForTesting = SafeEnterAsync(Current);
    }

    /// Idempotent — a Done-finish close and the window's own Closing event both route here.
    internal void RequestClose() {
        if (_closed) return;
        _closed = true;
        CloseRequested?.Invoke();
    }

    async Task NavigateAsync(WizardNavigation direction) {
        if (_navigating) return; // defense in depth — canExecute already blocks a bound button
        Navigating = true;
        try {
            if (!await SafeCanLeaveAsync(Current, direction)) return;

            if (direction == WizardNavigation.Next && _index == Steps.Count - 1) {
                RequestClose();
                return;
            }

            // Clamp, don't trust the arithmetic — a corrupted _index must never throw here.
            _index = Math.Clamp(_index + (direction == WizardNavigation.Back ? -1 : 1), 0, Steps.Count - 1);
            Current = Steps[_index];
            await SafeEnterAsync(Current);
        } finally {
            Navigating = false;
        }
    }

    // A throwing veto must not crash the app — treat it the same as an honest "no".
    async Task<bool> SafeCanLeaveAsync(IWizardStep step, WizardNavigation direction) {
        try {
            return await step.CanLeaveAsync(direction, _shutdownToken);
        } catch (OperationCanceledException) {
            return false;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: wizard step CanLeaveAsync failed unexpectedly: {ex.Message}");
            return false;
        }
    }

    // Entry side effects are best-effort — a throw here must not undo the transition already made.
    async Task SafeEnterAsync(IWizardStep step) {
        try {
            await step.OnEnterAsync(_shutdownToken);
        } catch (OperationCanceledException) {
            // shutdown mid-entry: nothing to roll back
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: wizard step OnEnterAsync failed unexpectedly: {ex.Message}");
        }
    }
}
