namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// Where a leg leaves its "this machine has gone" notice for an interrupt handler to find.
///
/// <para><b>A seam because the real one is process-global.</b> Without it every ordinary run writes
/// process state, which forces assembly-wide test exclusion on tests that have nothing to do with
/// interrupts.</para>
/// </summary>
public interface IFirstRunInterrupts {
    /// <summary>Registers the one notice this leg may send. Dispose to take it back.</summary>
    /// <param name="send">Sends one reason. Called at most once, by whichever path claims it.</param>
    /// <param name="interruptReason">What an INTERRUPT would say, evaluated at the moment it fires, or
    /// null for nothing to say. Separate from the leg's own reason deliberately — see
    /// <see cref="FirstRunNotice"/>.</param>
    IFirstRunNotice Arm(Func<string, CancellationToken, Task> send, Func<string?> interruptReason);
}

/// <summary>One send, claimed once, by whichever path gets there first.</summary>
public interface IFirstRunNotice : IDisposable {
    /// <summary>
    /// The leg's path: claims the send and performs it. A null <paramref name="reason"/> claims without
    /// sending, which is how a result that must not be relinquished also shuts the interrupt out. A no-op
    /// when an interrupt already claimed it.
    /// </summary>
    Task SendAsync(string? reason, CancellationToken ct);
}

/// <summary>
/// The single send, and the arbitration between the two paths that want to make it.
///
/// <para><b>Claim-then-send, never read-then-send.</b> Reading the callback and invoking it as separate
/// steps leaves both paths able to proceed, and the browser then shows whichever of two opposite remedies
/// landed last. One <see cref="Interlocked"/> exchange decides it.</para>
///
/// <para><b>Each claimant supplies its own reason, and neither may borrow the other's.</b> An interrupt
/// means the process is being killed, so nothing is carrying on however the leg's own result turned out;
/// an interrupt that sent the leg's reason could tell someone their terminal had taken over as that
/// terminal died — the one tail that states no remedy at all.</para>
///
/// <para><b>The loser waits rather than exiting through the winner.</b> An interrupt that lost the claim
/// would otherwise call <c>Environment.Exit</c> mid-POST and lose the notice altogether.</para>
/// </summary>
public sealed class FirstRunNotice(
        Func<string, CancellationToken, Task> send,
        Func<string?>                         interruptReason,
        Action<FirstRunNotice>?               onDispose = null) : IFirstRunNotice {
    readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    int _claimed;

    /// <inheritdoc/>
    public async Task SendAsync(string? reason, CancellationToken ct) {
        if (!TryClaim()) return;

        await RunAsync(reason, ct);
    }

    /// <summary>
    /// An interrupt handler's path, blocking for at most <paramref name="budget"/>. Swallows everything:
    /// the next statement is an exit, and there is no state left for a failure to change.
    /// </summary>
    public void RunBeforeExit(TimeSpan budget) {
        try {
            if (TryClaim()) {
                using var cts = new CancellationTokenSource(budget);

                RunAsync(interruptReason(), cts.Token).Wait(budget);
            } else {
                _sent.Task.Wait(budget);
            }
        } catch {
            // Best effort, by construction.
        }
    }

    public void Dispose() {
        onDispose?.Invoke(this);

        // Releases an interrupt waiting on a send that is never coming.
        _sent.TrySetResult();
    }

    bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;

    async Task RunAsync(string? reason, CancellationToken ct) {
        try {
            if (reason is not null) await send(reason, ct);
        } finally {
            _sent.TrySetResult();
        }
    }
}

/// <summary>
/// The process-global sink the CLI's signal handlers read. Ctrl+C, SIGTERM, SIGHUP and the
/// parent-liveness watchdog all reach <c>Environment.Exit</c>, which runs no <c>finally</c> anywhere, so
/// the notice has to be reachable from a handler that takes no arguments.
///
/// <para><b>Best effort, and bounded.</b> It usually lands. A SIGKILL, a lost network or a machine going
/// to sleep sends nothing, and the browser is then left waiting until the flow's own lifetime ends
/// it.</para>
///
/// <para>A test touching THIS needs bare <c>[NotInParallel]</c>; a test of a leg passes its own
/// <see cref="IFirstRunInterrupts"/> and needs none.</para>
/// </summary>
public static class FirstRunInterruptRelinquish {
    static FirstRunNotice? _pending;

    public static IFirstRunInterrupts Process { get; } = new ProcessSink();

    /// <summary>Sends whatever is armed, or waits out a send already in flight.</summary>
    public static void RunBeforeExit(TimeSpan budget) => Volatile.Read(ref _pending)?.RunBeforeExit(budget);

    sealed class ProcessSink : IFirstRunInterrupts {
        public IFirstRunNotice Arm(
                Func<string, CancellationToken, Task> send, Func<string?> interruptReason) {
            // Compares before clearing, so a second leg's registration is not dropped by the first leg's
            // handle going out of scope.
            var notice = new FirstRunNotice(
                send, interruptReason, onDispose: n => Interlocked.CompareExchange(ref _pending, null, n));

            Volatile.Write(ref _pending, notice);

            return notice;
        }
    }
}
