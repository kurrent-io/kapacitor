using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// Renders ONE lifecycle prompt (spec §6 dialogs) — no queue, unlike ConsentPromptViewModel:
/// LifecycleSurface's own SemaphoreSlim(1,1) already serializes ConfirmAsync calls one at a time,
/// so at most one of these is ever live at once. Accept/Decline resolve the caller's
/// TaskCompletionSource directly (constructor-injected, same shape the Task 21 interim window's
/// button handlers used) and then fire CloseRequested so the window can close itself — the same
/// split ConsentPromptWindow uses for its own CloseRequested subscription.
public sealed class LifecyclePromptViewModel : ReactiveObject {
    // Decision-7 disclosure (spec §3.6, §4.1): the terminal PATH could not be determined, so a
    // unit-writing mutation may not match the user's shell PATH.
    const string DegradedPathSentence =
        "The terminal PATH could not be determined — the reinstalled service may not match your shell's PATH.";

    readonly Subject<Unit> _closeRequested = new();

    public string Title { get; }
    public string Disclosure { get; }
    public bool PathDegraded { get; }

    /// Null when PathDegraded is false — bound with StringConverters.IsNotNullOrEmpty so the
    /// degraded-PATH line collapses instead of reserving dead space (ConsentPromptWindow's same
    /// pattern for its countdown/phase lines).
    public string? DegradedPathText => PathDegraded ? DegradedPathSentence : null;

    /// Fires once, on Accept or Decline — the window closes itself on this (spec §6); the
    /// ViewModel owns no window.
    public IObservable<Unit> CloseRequested => _closeRequested.AsObservable();

    public ReactiveCommand<Unit, Unit> AcceptCommand { get; }
    public ReactiveCommand<Unit, Unit> DeclineCommand { get; }

    public LifecyclePromptViewModel(LifecyclePrompt prompt, TaskCompletionSource<bool> tcs) {
        Title        = TitleFor(prompt.Kind);
        Disclosure   = prompt.Disclosure;
        PathDegraded = prompt.PathDegraded;

        AcceptCommand  = ReactiveCommand.Create(() => Resolve(tcs, true));
        DeclineCommand = ReactiveCommand.Create(() => Resolve(tcs, false));
    }

    void Resolve(TaskCompletionSource<bool> tcs, bool result) {
        // TrySetResult, not SetResult: a window closed via the titlebar/Esc after ct already
        // cancelled it (WireDialogCancellation, carried forward from Task 21) may have already
        // resolved this tcs — a click landing on the same beat must be a silent no-op, not a
        // throw.
        tcs.TrySetResult(result);
        _closeRequested.OnNext(Unit.Default);
    }

    internal static string TitleFor(string kind) => kind switch {
        LifecyclePrompt.KindRestartUpdate => "Restart daemon to update",
        LifecyclePrompt.KindTakeover      => "Take over daemon management",
        LifecyclePrompt.KindShim          => "Install command-line tool",
        _                                 => "Repair daemon service", // KindRepair and any future kind
    };
}
