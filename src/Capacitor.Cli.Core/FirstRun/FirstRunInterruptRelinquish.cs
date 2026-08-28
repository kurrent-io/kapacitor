namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// The one exit the browser leg cannot see: an interrupt handler calling <c>Environment.Exit</c>, which
/// runs no <c>finally</c> anywhere. This lets the handler spend a moment saying the machine has gone
/// before the process does.
///
/// <para><b>A static because the handlers are installed process-wide and take no arguments.</b> There is
/// nothing command-scoped for them to reach through, so the leg leaves a callback here for its own
/// duration and takes it back. A test touching this needs bare <c>[NotInParallel]</c> — it is
/// process-global state.</para>
///
/// <para><b>Best effort, and bounded.</b> It usually lands. A SIGKILL, a lost network or a machine going
/// to sleep sends nothing, and the browser then falls back to what it did before this existed: a screen
/// that keeps waiting, bounded by the flow's own lifetime.</para>
/// </summary>
public static class FirstRunInterruptRelinquish {
    static Func<CancellationToken, Task>? _pending;

    /// <summary>Registers what to send if the process is interrupted. Dispose to take it back — a leg that
    /// ended normally has already said its piece.</summary>
    public static IDisposable Arm(Func<CancellationToken, Task> relinquish) {
        Volatile.Write(ref _pending, relinquish);

        return new Disarm(relinquish);
    }

    /// <summary>
    /// Sends it, blocking for at most <paramref name="budget"/>. Called from a signal handler, so it
    /// swallows everything: the next statement is an exit, and there is no state left for a failure to
    /// change.
    /// </summary>
    public static void RunBeforeExit(TimeSpan budget) {
        if (Volatile.Read(ref _pending) is not { } relinquish) return;

        try {
            using var cts = new CancellationTokenSource(budget);

            relinquish(cts.Token).Wait(budget);
        } catch {
            // Best effort, by construction.
        }
    }

    /// <summary>Compares before clearing, so a second leg's registration is not dropped by the first
    /// leg's handle going out of scope.</summary>
    sealed class Disarm(Func<CancellationToken, Task> armed) : IDisposable {
        public void Dispose() => Interlocked.CompareExchange(ref _pending, null, armed);
    }
}
