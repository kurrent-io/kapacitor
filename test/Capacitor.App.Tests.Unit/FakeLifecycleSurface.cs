using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// Scripted ILifecycleSurface — records every Status/Attention line and every confirmation
/// prompt, so tests can assert exactly what the controller surfaced without any UI.
sealed class FakeLifecycleSurface : ILifecycleSurface {
    public readonly List<string> StatusMessages = [];
    public readonly List<string> AttentionMessages = [];
    public readonly List<LifecyclePrompt> Prompts = [];

    public Func<LifecyclePrompt, CancellationToken, Task<bool>> ConfirmBehavior = (_, _) => Task.FromResult(false);

    /// Scripts TryConfirmAsync's own answer (including null) independent of ConfirmBehavior, so a
    /// test can drive the "never reached the dialog" case without a real ct race.
    public Func<LifecyclePrompt, CancellationToken, Task<bool?>>? TryConfirmBehavior;

    public void Status(string message) => StatusMessages.Add(message);

    public void Attention(string message) => AttentionMessages.Add(message);

    public Task<bool> ConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) {
        Prompts.Add(prompt);
        return ConfirmBehavior(prompt, ct);
    }

    /// Mirrors LifecycleSurface's own contract: an already-cancelled ct never reaches the dialog — null, nothing recorded.
    public async Task<bool?> TryConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) {
        if (TryConfirmBehavior is { } behavior) {
            Prompts.Add(prompt);
            return await behavior(prompt, ct).ConfigureAwait(false);
        }
        if (ct.IsCancellationRequested) return null;
        Prompts.Add(prompt);
        return await ConfirmBehavior(prompt, ct).ConfigureAwait(false);
    }
}
