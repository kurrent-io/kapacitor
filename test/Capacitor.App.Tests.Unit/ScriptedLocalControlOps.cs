using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// Fake ILocalControlOps whose Get/Put/Stop calls are gated by per-call TaskCompletionSources: a
/// test arms the NEXT call's outcome before triggering it, so hold/release is explicit and
/// deterministic rather than timing-based. *Calls counters increment BEFORE the gate is awaited,
/// so they double as proof a call actually reached the ops layer (as opposed to being
/// dropped/ignored inside the caller). Mirrors ExchangeAsync's real already-cancelled-token
/// short-circuit (spec §10) so a permanently cancelled shutdown token behaves the same as the
/// real LocalControlOps. Shared by PauseControllerTests (Get/Put) and AgentActionServiceTests
/// (Stop) — one scripted fake for every ILocalControlOps consumer in Capacitor.App.
sealed class ScriptedLocalControlOps : ILocalControlOps {
    readonly Queue<TaskCompletionSource<ConsentPolicyDto>> _gets = new();
    readonly Queue<TaskCompletionSource<ConsentAckDto>> _puts = new();
    readonly Queue<TaskCompletionSource<StopAgentResult>> _stops = new();

    public int GetCalls;
    public int PutCalls;
    public int StopCalls;
    public readonly List<ConsentPolicyDto> PutPayloads = [];
    public readonly List<(string AgentId, bool Force)> StopPayloads = [];

    public TaskCompletionSource<ConsentPolicyDto> ArmGet() {
        var tcs = new TaskCompletionSource<ConsentPolicyDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gets.Enqueue(tcs);
        return tcs;
    }

    public void QueueGet(ConsentPolicyDto policy) => ArmGet().SetResult(policy);
    public void QueueGetFailure(string reason) => ArmGet().SetException(new LocalControlOpsException(reason, reason));
    public void QueueGetUnmappedFailure(Exception ex) => ArmGet().SetException(ex);

    public TaskCompletionSource<ConsentAckDto> ArmPut() {
        var tcs = new TaskCompletionSource<ConsentAckDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _puts.Enqueue(tcs);
        return tcs;
    }

    public void QueueAck(bool ok, string? error) => ArmPut().SetResult(new ConsentAckDto(ok, error));

    public TaskCompletionSource<StopAgentResult> ArmStop() {
        var tcs = new TaskCompletionSource<StopAgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stops.Enqueue(tcs);
        return tcs;
    }

    public void QueueStop(StopAgentResult result) => ArmStop().SetResult(result);
    public void QueueStopFailure(string reason) => ArmStop().SetException(new LocalControlOpsException(reason, reason));
    public void QueueStopUnmappedFailure(Exception ex) => ArmStop().SetException(ex);

    public Task<ConsentPolicyDto> GetConsentPolicyAsync(CancellationToken ct) {
        Interlocked.Increment(ref GetCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<ConsentPolicyDto>(ct);
        if (_gets.Count == 0) throw new InvalidOperationException("ScriptedLocalControlOps: unscripted Get call");
        var tcs = _gets.Dequeue();
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public Task<ConsentAckDto> PutConsentPolicyAsync(ConsentPolicyDto policy, CancellationToken ct) {
        Interlocked.Increment(ref PutCalls);
        PutPayloads.Add(policy);
        if (ct.IsCancellationRequested) return Task.FromCanceled<ConsentAckDto>(ct);
        if (_puts.Count == 0) throw new InvalidOperationException("ScriptedLocalControlOps: unscripted Put call");
        var tcs = _puts.Dequeue();
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public Task<StopAgentResult> StopAgentAsync(string agentId, bool force, CancellationToken ct) {
        Interlocked.Increment(ref StopCalls);
        StopPayloads.Add((agentId, force));
        if (ct.IsCancellationRequested) return Task.FromCanceled<StopAgentResult>(ct);
        if (_stops.Count == 0) throw new InvalidOperationException("ScriptedLocalControlOps: unscripted Stop call");
        var tcs = _stops.Dequeue();
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }
}
