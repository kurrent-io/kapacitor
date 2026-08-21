using ReactiveUI;

namespace Capacitor.App.ViewModels.Onboarding;

/// One line of the Done step's summary (spec §3 step 8). A dedicated record rather than a
/// ValueTuple — Avalonia's reflection-based bindings need real CLR properties, not a tuple's
/// compiler-only element-name aliases.
public sealed record DoneSummaryEntry(string Title, bool Satisfied, string? Note) {
    public string Glyph => Satisfied ? "✓" : "—";
}

/// spec §3 step 8: a summary of what the earlier steps set up and what was skipped, and why.
/// Dumb by design — the composition root aggregates every other step's Satisfied/skip state and
/// supplies the why-skipped notes ("kcap CLI not found", "requires sign-in", ...).
public sealed class DoneStepViewModel : ReactiveObject, IWizardStep {
    readonly Func<IReadOnlyList<(string Title, bool Satisfied, string? Note)>> _summaryProvider;

    public DoneStepViewModel(Func<IReadOnlyList<(string Title, bool Satisfied, string? Note)>> summaryProvider) =>
        _summaryProvider = summaryProvider;

    public WizardStepId Id         => WizardStepId.Done;
    public string       Title      => "You're all set";
    public bool         Applicable => true;
    public bool         Satisfied  => true; // a summary step is never itself incomplete

    public IReadOnlyList<DoneSummaryEntry> Summary =>
        _summaryProvider().Select(e => new DoneSummaryEntry(e.Title, e.Satisfied, e.Note)).ToList();

    /// Re-renders on every entry: Back-then-forward can change other steps' state in between,
    /// and Summary is recomputed fresh from the provider on every read.
    public Task OnEnterAsync(CancellationToken ct) {
        this.RaisePropertyChanged(nameof(Summary));

        return Task.CompletedTask;
    }

    // Next finishes the wizard — the shell's own last-step handling raises CloseRequested.
    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) => Task.FromResult(true);
}
