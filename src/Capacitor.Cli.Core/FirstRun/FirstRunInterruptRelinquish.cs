namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// Where a leg leaves its "this machine has gone" callback for an interrupt handler to find.
///
/// <para><b>A seam because the real one is process-global.</b> Without it every ordinary run writes
/// process state, which forces assembly-wide test exclusion on tests that have nothing to do with
/// interrupts.</para>
/// </summary>
public interface IFirstRunInterrupts {
    /// <summary>Registers what to send if the process is interrupted. Dispose to take it back.</summary>
    IDisposable Arm(Func<CancellationToken, Task> relinquish);
}

/// <summary>
/// The one exit the browser leg cannot see: an interrupt handler calling <c>Environment.Exit</c>, which
/// runs no <c>finally</c> anywhere. This lets the handler spend a moment saying the machine has gone
/// before the process does.
///
/// <para><b>A static because the handlers are installed process-wide and take no arguments.</b> There is
/// nothing command-scoped for them to reach through, so the leg leaves a callback here for its own
/// duration and takes it back. A test touching THIS needs bare <c>[NotInParallel]</c>; a test of a leg
/// passes its own <see cref="IFirstRunInterrupts"/> and needs none.</para>
///
/// <para><b>Best effort, and bounded.</b> It usually lands. A SIGKILL, a lost network or a machine going
/// to sleep sends nothing, and the browser is then left waiting until the flow's own lifetime ends
/// it.</para>
/// </summary>
public static class FirstRunInterruptRelinquish {
    static Func<CancellationToken, Task>? _pending;

    /// <summary>The process-global sink, which is what the CLI's signal handlers read.</summary>
    public static IFirstRunInterrupts Process { get; } = new ProcessSink();

    sealed class ProcessSink : IFirstRunInterrupts {
        public IDisposable Arm(Func<CancellationToken, Task> relinquish) =>
            FirstRunInterruptRelinquish.Arm(relinquish);
    }

    /// <inheritdoc cref="IFirstRunInterrupts.Arm"/>
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
