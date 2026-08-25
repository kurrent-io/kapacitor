using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// Scriptable ITerminalAttachClient: one instance per attach attempt, mirroring
/// AgentAttachClient's own "one client = one run, never re-run after termination" contract.
/// RunAsync blocks on <see cref="Result"/> until the test completes it (an outcome or a fault);
/// TriggerAttached/TriggerOutput invoke the callbacks the factory captured, exactly like the
/// production client's own read-loop would.
sealed class FakeTerminalAttachClient : ITerminalAttachClient {
    // NOT readonly: DisposeAsync clears these -- a real client's callbacks close over the
    // attempt's surface/decoder, and this client is the only thing still referencing them once
    // the VM has nulled its own Surface/_client fields, so a test proving the surface is released
    // (weak-reference goes dead after GC.Collect) needs this hold broken too, exactly like the
    // production client dropping its own captured state on Dispose.
    Func<byte[], string?, CancellationToken, Task>? _onAttached;
    Func<byte[], CancellationToken, Task>? _onOutput;

    /// Cancelled by DisposeAsync (mirrors AgentAttachClient's internal `_lifetime`) and linked
    /// from RunAsync's own ct -- so either the caller's token OR a Dispose releases anything
    /// awaiting it, same as the production client.
    CancellationTokenSource? _lifetime;

    public readonly TaskCompletionSource<AttachOutcome> Result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public readonly TaskCompletionSource RunStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<byte[]> SentInput { get; } = [];
    public List<(int Cols, int Rows)> Resizes { get; } = [];
    public int DetachCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// When true, RunAsync blocks on an internal never-completing await (standing in for a pump
    /// that's awaiting a callback that never returns) released only by _lifetime -- i.e. only by
    /// DisposeAsync or the run's own token, exactly like a real socket-read pump.
    public bool HangOnOutputForever { get; set; }

    /// The Task RunAsync is blocked on when HangOnOutputForever is set -- exposed so a test can
    /// assert it completed (cancelled) after teardown, independent of any GC-based assertion.
    public Task? CallbackTask { get; private set; }

    /// When true, DetachAsync blocks until the test completes DetachGate (an outcome or a fault)
    /// -- deliberately NOT tied to _lifetime, so TeardownAsync's 1s bound is what has to force it,
    /// not an incidental Dispose-driven unblock.
    public bool HangDetachForever { get; set; }
    public readonly TaskCompletionSource DetachGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// When set, DisposeAsync awaits this gate before RETURNING -- letting a test hold "dispose in
    /// flight" open deterministically, the same way a real client's DisposeAsync awaits its own
    /// pump. Null (default) means DisposeAsync returns immediately once this class's own cleanup
    /// runs, preserving every test written before this existed.
    public TaskCompletionSource? DisposeGate { get; set; }

    public FakeTerminalAttachClient(
            Func<byte[], string?, CancellationToken, Task> onAttached,
            Func<byte[], CancellationToken, Task> onOutput) {
        _onAttached = onAttached;
        _onOutput = onOutput;
    }

    public async Task<AttachOutcome> RunAsync(int initialCols, int initialRows, CancellationToken ct) {
        Cols = initialCols;
        Rows = initialRows;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        RunStarted.TrySetResult();

        if (HangOnOutputForever) {
            CallbackTask = Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token);
            await CallbackTask.ConfigureAwait(false); // throws OperationCanceledException once released
        }

        return await Result.Task.ConfigureAwait(false);
    }

    /// Invokes the captured onAttached callback with the run's own token, exactly like the
    /// production client's read loop does.
    public Task TriggerAttached(byte[] snapshot, string? reason = null) =>
        _onAttached is null
            ? throw new ObjectDisposedException(nameof(FakeTerminalAttachClient))
            : _onAttached(snapshot, reason, _lifetime?.Token ?? CancellationToken.None);

    /// Invokes the captured onOutput callback with the run's own token.
    public Task TriggerOutput(byte[] bytes) =>
        _onOutput is null
            ? throw new ObjectDisposedException(nameof(FakeTerminalAttachClient))
            : _onOutput(bytes, _lifetime?.Token ?? CancellationToken.None);

    public Task SendInputAsync(byte[] bytes) {
        SentInput.Add(bytes);
        return Task.CompletedTask;
    }

    public Task ResizeAsync(int cols, int rows) {
        Resizes.Add((cols, rows));
        return Task.CompletedTask;
    }

    public async Task DetachAsync() {
        DetachCalls++;
        if (HangDetachForever) await DetachGate.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() {
        DisposeCalls++;
        _lifetime?.Cancel();
        _onAttached = null;
        _onOutput = null;
        // Mirrors AgentAttachClient.DisposeAsync's "one eager local terminalizer" (TryClaim a
        // Detached outcome) -- a TRY, so it's a silent no-op if the test (or an earlier caller)
        // already completed Result with something else.
        Result.TrySetResult(new AttachOutcome.Detached());
        if (DisposeGate is not null) await DisposeGate.Task.ConfigureAwait(false);
    }
}

/// Records every client it creates, and the callbacks the VM handed it -- the seam tests use to
/// drive attach attempts from outside (TriggerAttached/TriggerOutput/Result) without a real daemon.
sealed class FakeTerminalAttachClientFactory {
    public List<FakeTerminalAttachClient> Created { get; } = [];

    /// Configures the NEXT client this factory creates (consumed once) -- lets a test arrange
    /// HangOnOutputForever/HangDetachForever before the VM triggers the attach that creates it.
    public Action<FakeTerminalAttachClient>? ConfigureNext { get; set; }

    public TerminalAttachClientFactory Factory => (agentId, onAttached, onOutput) => {
        var client = new FakeTerminalAttachClient(onAttached, onOutput);
        ConfigureNext?.Invoke(client);
        ConfigureNext = null;
        Created.Add(client);
        return client;
    };
}
