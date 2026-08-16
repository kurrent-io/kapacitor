using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;

namespace Capacitor.App.ViewModels.Onboarding;

/// Back/Next/Skip all funnel through Current's CanLeaveAsync veto; Next on the last step finishes.
public sealed class OnboardingViewModel : ReactiveObject {
    readonly CancellationToken _shutdownToken;
    bool _closed;

    public IReadOnlyList<IWizardStep> Steps { get; }

    int _index;

    IWizardStep _current;
    public IWizardStep Current {
        get => _current;
        private set => this.RaiseAndSetIfChanged(ref _current, value);
    }

    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipCommand { get; }

    /// Fires once per logical close: the Done step's finish, or the window closing.
    public event Action? CloseRequested;

    /// The constructor's own OnEnterAsync(Steps[0]) call, exposed so a test can await it deterministically.
    internal Task PendingEnterForTesting { get; }

    public OnboardingViewModel(IEnumerable<IWizardStep> steps, CancellationToken shutdownToken = default) {
        _shutdownToken = shutdownToken;
        Steps = steps.Where(s => s.Applicable).ToList();
        if (Steps.Count == 0) throw new ArgumentException("at least one applicable step is required", nameof(steps));

        _current = Steps[0];

        var currentChanged = this.WhenAnyValue(x => x.Current);
        var canBack = currentChanged.Select(_ => _index > 0);
        var canSkip = currentChanged.Select(_ => _index < Steps.Count - 1);

        BackCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Back), canBack);
        SkipCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Skip), canSkip);
        NextCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Next));

        PendingEnterForTesting = Current.OnEnterAsync(_shutdownToken);
    }

    /// Idempotent — a Done-finish close and the window's own Closing event both route here.
    internal void RequestClose() {
        if (_closed) return;
        _closed = true;
        CloseRequested?.Invoke();
    }

    async Task NavigateAsync(WizardNavigation direction) {
        if (!await Current.CanLeaveAsync(direction, _shutdownToken)) return;

        if (direction == WizardNavigation.Next && _index == Steps.Count - 1) {
            RequestClose();
            return;
        }

        _index += direction == WizardNavigation.Back ? -1 : 1;
        Current = Steps[_index];
        await Current.OnEnterAsync(_shutdownToken);
    }
}
