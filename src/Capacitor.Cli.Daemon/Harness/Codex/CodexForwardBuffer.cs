using System.Threading.Channels;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// The §2.4 bounded forward buffer that decouples the codex app-server stdout reader from SignalR
/// envelope forwarding. Envelopes are emitted synchronously from the single notification-handling path
/// (the app-server read loop); the SignalR forwarder drains <see cref="Reader"/> independently.
///
/// <para>Overflow policy, fixed by §2.4:</para>
/// <list type="bullet">
/// <item><description><b>Canonical envelopes are NEVER dropped.</b> When the buffer is full a canonical
/// emit BLOCKS the caller — the read loop stops consuming stdout and the app-server blocks on its
/// writes, which is lossless (a slow reader loses nothing; the app-server buffers/blocks). A gap in the
/// canonical stream would corrupt the transcript permanently; a stall does not.</description></item>
/// <item><description><b>Ephemeral envelopes are DROPPABLE.</b> When the buffer is full an ephemeral
/// emit is dropped rather than blocking (counted in <see cref="DroppedEphemeralCount"/>). The ephemeral
/// payloads are cumulative content-so-far and the item's canonical completed snapshot converges the
/// viewer, so a dropped ephemeral is harmless.</description></item>
/// <item><description><b>Stall watchdog.</b> If a canonical emit stays blocked past
/// <c>forwardStallSeconds</c>, the buffer fires <c>onStall</c> ONCE (a deterministic terminal fault) and
/// stops accepting — never an indefinite wedge, and never a silently incomplete canonical transcript
/// (the fault makes the truncation loud).</description></item>
/// </list>
/// Not thread-safe for concurrent emit (single writer = the read loop); the reader side is a standard
/// channel reader consumed by one forwarder.
/// </summary>
internal sealed class CodexForwardBuffer : IDisposable {
    readonly Channel<AcpEventEnvelope> _channel;
    readonly TimeSpan          _stallTimeout;
    readonly CancellationToken _shutdown;
    readonly Action<TimeSpan>  _onStall;
    int _droppedEphemeral;
    int _stalled;

    public CodexForwardBuffer(int capacity, TimeSpan stallTimeout, CancellationToken shutdown, Action<TimeSpan> onStall) {
        _channel = Channel.CreateBounded<AcpEventEnvelope>(new BoundedChannelOptions(capacity) {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _stallTimeout = stallTimeout;
        _shutdown     = shutdown;
        _onStall      = onStall;
    }

    /// <summary>The forwarder's drain side — FIFO, real seq assigned downstream (envelopes carry the
    /// placeholder <c>Seq=0</c>).</summary>
    public ChannelReader<AcpEventEnvelope> Reader => _channel.Reader;

    /// <summary>Ephemeral envelopes dropped because the buffer was full — a liveness-only loss, never a
    /// transcript gap.</summary>
    public int DroppedEphemeralCount => Volatile.Read(ref _droppedEphemeral);

    /// <summary>True once the stall watchdog has fired; further emits are no-ops (the agent is being
    /// faulted and its canonical tail is deliberately truncated, loudly, by <c>onStall</c>).</summary>
    public bool Stalled => Volatile.Read(ref _stalled) == 1;

    /// <summary>Emit one envelope from the read loop. Canonical blocks when full (backpressure);
    /// ephemeral drops when full. A no-op once stalled.</summary>
    public void Emit(AcpEventEnvelope env) {
        if (Stalled) return;

        if (env.Ephemeral) {
            if (!_channel.Writer.TryWrite(env)) Interlocked.Increment(ref _droppedEphemeral);
            return;
        }

        // Canonical: never dropped. TryWrite is the fast path when there is room.
        if (_channel.Writer.TryWrite(env)) return;
        WriteCanonicalBlocking(env);
    }

    void WriteCanonicalBlocking(AcpEventEnvelope env) {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        stall.CancelAfter(_stallTimeout);
        try {
            // Blocks the read-loop thread until space frees — the app-server blocks on stdout (lossless).
            _channel.Writer.WriteAsync(env, stall.Token).AsTask().GetAwaiter().GetResult();
        } catch (OperationCanceledException) {
            // Shutdown fired mid-wait: we are tearing down, so drop this envelope silently (the session
            // is ending anyway) rather than propagating out of the read-loop notification handler.
            if (_shutdown.IsCancellationRequested) return;
            // Otherwise the buffer stayed full past the stall timeout → deterministic terminal fault. The
            // undrained canonical tail is lost by design and reported loudly by onStall (never silently).
            if (Interlocked.Exchange(ref _stalled, 1) == 0) _onStall(_stallTimeout);
        }
    }

    /// <summary>Signals the reader no more envelopes will arrive (session ended / teardown).</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public void Dispose() => _channel.Writer.TryComplete();
}
