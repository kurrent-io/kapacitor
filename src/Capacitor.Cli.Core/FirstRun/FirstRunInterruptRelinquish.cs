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
    IFirstRunNotice Arm(Func<CancellationToken, Task> send);
}

/// <summary>
/// One send, claimed once, by whichever path gets there first.
///
/// <para><b>Two paths race for it and they must not both win.</b> The leg sends the reason its result
/// names; an interrupt handler sends whatever the reason is at that instant, because
/// <c>Environment.Exit</c> runs no <c>finally</c> and there is nothing else between it and a browser left
/// offering decisions nobody will act on. The two reasons give opposite remedies, so a second send is not
/// a duplicate — it is a contradiction.</para>
/// </summary>
public interface IFirstRunNotice : IDisposable {
    /// <summary>The leg's path: claims the send and performs it. A no-op when an interrupt already
    /// claimed it — its reason is the one that stands, because the process is going away.</summary>
    Task SendAsync(CancellationToken ct);
}

/// <summary>
/// The single send, and the arbitration between the two paths that want to make it.
///
/// <para><b>Claim-then-send, never read-then-send.</b> Reading the callback and invoking it as separate
/// steps leaves both paths able to proceed: the reader holds its reference while the other path disarms
/// and sends its own reason, and the browser then shows whichever of two opposite remedies lands last.
/// One <see cref="Interlocked"/> exchange decides it.</para>
///
/// <para><b>The loser waits rather than exiting through the winner.</b> An interrupt that lost the claim
/// would otherwise call <c>Environment.Exit</c> mid-POST and lose the notice altogether.</para>
/// </summary>
public sealed class FirstRunNotice(
        Func<CancellationToken, Task> send,
        Action<FirstRunNotice>?       onDispose = null) : IFirstRunNotice {
    readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    int _claimed;

    /// <inheritdoc/>
    public async Task SendAsync(CancellationToken ct) {
        if (!TryClaim()) return;

        await RunAsync(ct);
    }

    /// <summary>
    /// An interrupt handler's path, blocking for at most <paramref name="budget"/>. Swallows everything:
    /// the next statement is an exit, and there is no state left for a failure to change.
    /// </summary>
    public void RunBeforeExit(TimeSpan budget) {
        try {
            if (TryClaim()) {
                using var cts = new CancellationTokenSource(budget);

                RunAsync(cts.Token).Wait(budget);
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

    async Task RunAsync(CancellationToken ct) {
        try {
            await send(ct);
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
        public IFirstRunNotice Arm(Func<CancellationToken, Task> send) {
            // Compares before clearing, so a second leg's registration is not dropped by the first leg's
            // handle going out of scope.
            var notice = new FirstRunNotice(
                send, onDispose: n => Interlocked.CompareExchange(ref _pending, null, n));

            Volatile.Write(ref _pending, notice);

            return notice;
        }
    }
}
