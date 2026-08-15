namespace Capacitor.App.Services;

/// Production ILifecycleSurface (Task 22, replacing Task 21's interim NotifierLifecycleSurface).
/// Status/Attention push straight into constructor-supplied sinks — the composition root
/// (App.axaml.cs) wires these to MainWindowViewModel's start-message lane and TrayViewModel's
/// attention stream. ConfirmAsync shows a dialog via the supplied factory (the composition root
/// builds the real LifecyclePromptWindow/LifecyclePromptViewModel there — this class stays
/// Avalonia-free and unit-testable) and serializes calls with a SemaphoreSlim(1,1): spec §5's
/// "dialogs never stack" rule, and the same gate Task 24's shim offer reuses to never appear over
/// a live skew/repair dialog.
public sealed class LifecycleSurface(
        Action<string> setStatus, Action<string> setAttention,
        Func<LifecyclePrompt, CancellationToken, Task<bool>> showPrompt) : ILifecycleSurface {
    readonly SemaphoreSlim _gate = new(1, 1);

    public void Status(string message) => setStatus(message);
    public void Attention(string message) => setAttention(message);

    public async Task<bool> ConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) =>
        await TryConfirmAsync(prompt, ct).ConfigureAwait(false) ?? false;

    public async Task<bool?> TryConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) {
        try {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return null; // never got to show a dialog — distinct from a shown-and-declined one (ConfirmAsync degrades this to false)
        }

        try {
            return await showPrompt(prompt, ct).ConfigureAwait(false);
        } finally {
            // Released on EVERY path, including a ct-cancel that resolved the dialog false —
            // otherwise a cancelled dialog would hold the gate forever and deadlock every
            // ConfirmAsync queued behind it (the exact bug class Task 21's fix-round-1 closed for
            // the single-dialog case; this is its two-dialog extension).
            _gate.Release();
        }
    }
}
