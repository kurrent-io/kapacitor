namespace Capacitor.Cli.Core.LocalIpc;

using System.Net.Sockets;

public sealed class AgentAttachClient : IAsyncDisposable {
    // The single linearization point: first CAS winner decides RunAsync's result.
    // Values: AttachOutcome | Exception (callback fault) | _cancelledSentinel.
    object? _cause;
    static readonly object CancelledSentinel = new();

    readonly string _socketPath;
    readonly string _agentId;
    readonly Func<byte[], string?, CancellationToken, Task> _onAttached;
    readonly Func<byte[], CancellationToken, Task> _onOutput;
    readonly Action<string, Exception>? _diagnostics;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly object _sinkLock = new();

    Socket? _socket;
    NetworkStream? _stream;
    CancellationTokenSource? _lifetime;   // linked to the caller token; callbacks get this token
    volatile bool _detachRequested;       // intent, never a cause
    volatile bool _attachedAny;           // true after either Attached reply
    Task<AttachOutcome>? _run;

    public AgentAttachClient(
            string socketPath, string agentId,
            Func<byte[], string?, CancellationToken, Task> onAttachedAsync,
            Func<byte[], CancellationToken, Task> onOutputAsync,
            Action<string, Exception>? diagnostics = null) {
        _socketPath = socketPath;
        _agentId = agentId;
        _onAttached = onAttachedAsync;
        _onOutput = onOutputAsync;
        _diagnostics = diagnostics;
    }

    bool TryClaim(object cause) => Interlocked.CompareExchange(ref _cause, cause, null) is null;

    // Losing exceptions go here exactly once; expected teardown artifacts are
    // excluded by the callers. Serialized; a throwing sink is contained.
    void Report(string context, Exception ex) {
        if (_diagnostics is null) return;
        lock (_sinkLock) {
            try { _diagnostics(context, ex); } catch { /* contained by contract */ }
        }
    }

    public Task<AttachOutcome> RunAsync(int initialCols, int initialRows, CancellationToken ct) {
        // Dispose/Detach before Run: terminal immediately, no dialing.
        if (_cause is AttachOutcome pre) return Task.FromResult(pre);
        if (_detachRequested) { TryClaim(new AttachOutcome.Detached()); return Task.FromResult((AttachOutcome)_cause!); }
        return _run = RunCoreAsync(initialCols, initialRows, ct);
    }

    async Task<AttachOutcome> RunCoreAsync(int cols, int rows, CancellationToken ct) {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var reg = ct.Register(() => { if (TryClaim(CancelledSentinel)) _lifetime.Cancel(); });
        try {
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await _socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct).ConfigureAwait(false);
            _stream = new NetworkStream(_socket, ownsSocket: false);
            await FrameCodec.WriteAsync(_stream, new LocalFrame(FrameType.Attach) { Text = _agentId }, ct).ConfigureAwait(false);

            while (true) {
                var frame = await FrameCodec.ReadAsync(_stream, CancellationToken.None).ConfigureAwait(false);
                if (frame is null) {                                     // clean EOF
                    TryClaim(_detachRequested ? new AttachOutcome.Detached() : new AttachOutcome.ConnectionLost());
                    break;
                }
                switch (frame.Type) {
                    case FrameType.Attached: {
                        var (_, snapshot) = FrameCodec.Attached(frame);
                        _attachedAny = true;
                        await _onAttached(snapshot, null, _lifetime.Token).ConfigureAwait(false);
                        await WriteLockedAsync(SizeFrame(cols, rows)).ConfigureAwait(false);  // repaint nudge
                        break;
                    }
                    case FrameType.AttachedReadOnly: {
                        var (_, reason, snapshot) = FrameCodec.AttachedReadOnly(frame);
                        _attachedAny = true;
                        await _onAttached(snapshot, reason, _lifetime.Token).ConfigureAwait(false);
                        break;                                            // no nudge: never influence the clamp
                    }
                    case FrameType.Stdout:
                        await _onOutput(frame.Bytes, _lifetime.Token).ConfigureAwait(false);
                        break;
                    case FrameType.Exited:
                        TryClaim(new AttachOutcome.Exited(frame.ExitCode));
                        goto done;
                    case FrameType.Error:
                        TryClaim(new AttachOutcome.Failed(frame.Text));
                        goto done;
                    default:
                        TryClaim(new AttachOutcome.Failed($"protocol failure: unexpected frame {frame.Type}"));
                        goto done;
                }
            }
            done: ;
        } catch (Exception ex) {
            ClassifyPumpException(ex);      // Task 5 fills this in fully
        } finally {
            CloseTransport();
        }
        return Project();
    }

    // Task 5 completes classification; Task 4 needs only enough for its tests.
    void ClassifyPumpException(Exception ex) {
        if (_detachRequested || _cause is not null) { /* local close or already decided */ }
        else if (!_attachedAny) TryClaim(new AttachOutcome.Failed(ex.Message));
        else TryClaim(new AttachOutcome.ConnectionLost());
        if (_cause is AttachOutcome && _cause is not AttachOutcome.Detached && ex is not OperationCanceledException)
            Report("attach pump", ex);   // refine in Task 7 per loser rules
    }

    AttachOutcome Project() =>
        _cause switch {
            AttachOutcome o => o,
            Exception fault => throw new AttachCallbackException(fault),   // Task 5 refines
            _ when ReferenceEquals(_cause, CancelledSentinel) => throw new OperationCanceledException(),
            _ => new AttachOutcome.ConnectionLost(),
        };

    static LocalFrame SizeFrame(int cols, int rows) => LocalFrame.Resize((ushort)cols, (ushort)rows);

    async Task WriteLockedAsync(LocalFrame frame) {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await FrameCodec.WriteAsync(_stream!, frame, CancellationToken.None).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    void CloseTransport() { try { _stream?.Dispose(); } catch { } try { _socket?.Dispose(); } catch { } }

    // Tasks 5-7 implement these fully; Task 4 ships minimal versions that keep
    // the signatures compiling.
    public Task SendInputAsync(byte[] bytes) => throw new NotImplementedException("Task 6");
    public Task ResizeAsync(int cols, int rows) => throw new NotImplementedException("Task 6");
    public Task DetachAsync() => throw new NotImplementedException("Task 5");
    public async ValueTask DisposeAsync() {
        _detachRequested = true;
        TryClaim(new AttachOutcome.Detached());
        _lifetime?.Cancel();
        CloseTransport();
        if (_run is { } run) { try { await run.ConfigureAwait(false); } catch { /* Task 5 refines */ } }
    }
}

/// Wraps a callback fault so RunAsync's fault is distinguishable from infrastructure exceptions.
public sealed class AttachCallbackException(Exception inner) : Exception("attach callback failed", inner);
