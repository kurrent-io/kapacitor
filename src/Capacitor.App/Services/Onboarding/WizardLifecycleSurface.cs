using ReactiveUI.Reactive;

namespace Capacitor.App.Services.Onboarding;

/// ILifecycleSurface for wizard-first mode: decision 2 builds no tray and no main window while
/// the wizard is open, so Status/Attention become step-local observable text instead of the
/// tray/start-message lanes, and confirmations open a dialog windowed over OnboardingWindow. The
/// same ConsumeMutationOutcomesAsync consumer therefore runs unchanged during onboarding. Dialog
/// serialization is delegated to LifecycleSurface rather than re-implemented — one never-stack
/// gate, one implementation.
public sealed class WizardLifecycleSurface : ReactiveObject, ILifecycleSurface {
    readonly LifecycleSurface _inner;

    string? _statusText;
    string? _attentionText;

    /// <param name="post">Marshals the text updates onto the UI thread — the outcome consumer runs off it.</param>
    public WizardLifecycleSurface(Func<LifecyclePrompt, CancellationToken, Task<bool>> showPrompt, Action<Action> post) =>
        _inner = new LifecycleSurface(
            message => post(() => StatusText = message),
            message => post(() => AttentionText = message),
            showPrompt);

    public string? StatusText {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// Kept apart from StatusText so the wizard can render a repair/attention line distinctly.
    public string? AttentionText {
        get => _attentionText;
        private set => this.RaiseAndSetIfChanged(ref _attentionText, value);
    }

    public void Status(string message) => _inner.Status(message);

    public void Attention(string message) => _inner.Attention(message);

    public Task<bool> ConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) => _inner.ConfirmAsync(prompt, ct);

    public Task<bool?> TryConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) => _inner.TryConfirmAsync(prompt, ct);
}
