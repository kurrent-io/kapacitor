using System.Reactive;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// The app's server-side clients as one set with one cleanup. Ownership is here: the holder takes
/// the two clients and owns the subject itself, with the cleanup task memoized, so the
/// startup-failure and shutdown paths can both reach it, sequentially or overlapping, and nothing
/// is disposed twice. The sequence itself is the static below, which is deliberately not idempotent.
public sealed class ServerClients : IAsyncDisposable {
    readonly Subject<Unit> _signIn = new();
    readonly Lazy<Task> _cleanup;
    volatile bool _cleanupRequested;

    public ServerClients(IAsyncDisposable? launch, IAsyncDisposable? workContext) =>
        _cleanup = new Lazy<Task>(() => CleanupAsync(launch, workContext, _signIn), LazyThreadSafetyMode.ExecutionAndPublication);

    public IObservable<Unit> SignInCompleted => _signIn;

    // IsValueCreated flips only after the factory returns — a cleanup completing synchronously
    // could otherwise dispose the subject before a racing NotifySignInCompleted sees it started.
    public bool CleanupStarted => _cleanupRequested || _cleanup.IsValueCreated;

    /// Raised where the app learns a sign-in completed. Ignored once cleanup has started: the
    /// subject is completed and disposed in the sequence, and a disposed subject throws on OnNext.
    public void NotifySignInCompleted() {
        if (CleanupStarted) return;
        _signIn.OnNext(Unit.Default);
    }

    public ValueTask DisposeAsync() {
        _cleanupRequested = true;
        return new(_cleanup.Value);
    }

    /// Launch client, then the work-context source, then the subject completed and disposed —
    /// each step guarded so a throwing disposal never skips the next.
    internal static async Task CleanupAsync(IAsyncDisposable? launch, IAsyncDisposable? workContext, Subject<Unit> signIn) {
        await DisposeGuarded(launch, "launch client").ConfigureAwait(false);
        await DisposeGuarded(workContext, "work-context source").ConfigureAwait(false);
        try {
            signIn.OnCompleted();
            signIn.Dispose();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app failed to complete the sign-in signal during teardown: {ex}");
        }
    }

    static async Task DisposeGuarded(IAsyncDisposable? disposable, string what) {
        if (disposable is null) return;
        try {
            await disposable.DisposeAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app failed to dispose the {what} during teardown: {ex}");
        }
    }
}
