using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

static class PermissionEntries {
    public static PendingPermissionRequest Entry(
            string requestId = "r1", string agentId = "a1", string vendor = "claude", string toolName = "Bash",
            string? toolInputJson = """{"command":"ls"}""", bool omitted = false, string requestedAt = "2026-08-28T10:00:00.0000000+00:00",
            string? toolUseId = null) {
        System.Text.Json.JsonElement? input = null;
        if (toolInputJson is not null) { using var d = System.Text.Json.JsonDocument.Parse(toolInputJson); input = d.RootElement.Clone(); }
        return new PendingPermissionRequest(new PermissionPendingDto(requestId, agentId, "s1", vendor, toolName, input, null, omitted, false, requestedAt, toolUseId));
    }

    public static PendingPermissionRequest Question(
            string requestId = "q1", string agentId = "a1",
            string toolInputJson = """{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]}""",
            string requestedAt = "2026-08-28T10:00:00.0000000+00:00") =>
        Entry(requestId, agentId, "claude", ClaudeElicitation.ToolName, toolInputJson, false, requestedAt);
}

/// Scripted IPermissionService: a real SourceCache plus a per-call outcome queue, like
/// FakeConsentService. A conclusive outcome evicts its target before the caller resumes; a
/// transport failure keeps it.
sealed class FakePermissionService : IPermissionService {
    public readonly SourceCache<PendingPermissionRequest, string> Cache = new(p => p.RequestId);
    readonly Queue<TaskCompletionSource<PermissionResolveOutcome>> _outcomes = new();
    public readonly List<(string RequestId, PermissionAnswer Answer)> Resolved = [];
    public readonly List<(string RequestId, IReadOnlyList<ElicitationAnswer> Answers)> Answered = [];

    public IObservable<IChangeSet<PendingPermissionRequest, string>> Pending => Cache.Connect();
    public IObservable<int> PendingCount => Cache.CountChanged;
    public IObservable<IReadOnlySet<string>> AgentsWithPending =>
        Cache.Connect().QueryWhenChanged(q => (IReadOnlySet<string>)q.Items.Select(p => p.AgentId).ToHashSet(StringComparer.Ordinal))
            .StartWith((IReadOnlySet<string>)new HashSet<string>());
    public IObservable<PendingSummary> Summary =>
        Cache.Connect()
            .QueryWhenChanged(q => PendingSummary.From(q.Items))
            .StartWith(new PendingSummary(0, 0));

    public void Add(PendingPermissionRequest entry) => Cache.AddOrUpdate(entry);
    public void Remove(string requestId) => Cache.Remove(requestId);

    public TaskCompletionSource<PermissionResolveOutcome> Arm() {
        var tcs = new TaskCompletionSource<PermissionResolveOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _outcomes.Enqueue(tcs);
        return tcs;
    }
    public void Queue(PermissionResolveKind kind, string? error = null) => Arm().SetResult(new PermissionResolveOutcome(kind, error));

    public async Task<PermissionResolveOutcome> ResolveAsync(PendingPermissionRequest target, PermissionAnswer answer, CancellationToken ct) {
        Resolved.Add((target.RequestId, answer));
        if (_outcomes.Count == 0) throw new InvalidOperationException("FakePermissionService: unscripted resolve call");
        var outcome = await _outcomes.Dequeue().Task;
        if (outcome.Kind != PermissionResolveKind.TransportFailure) Cache.Remove(target.RequestId);
        return outcome;
    }

    public async Task<PermissionResolveOutcome> AnswerAsync(PendingPermissionRequest target, IReadOnlyList<ElicitationAnswer> answers, CancellationToken ct) {
        if (target.Questions is null) throw new ArgumentException("not an elicitation entry", nameof(target));
        Answered.Add((target.RequestId, answers));
        if (_outcomes.Count == 0) throw new InvalidOperationException("FakePermissionService: unscripted answer call");
        var outcome = await _outcomes.Dequeue().Task;
        if (outcome.Kind != PermissionResolveKind.TransportFailure) Cache.Remove(target.RequestId);
        return outcome;
    }

    public void Dispose() => Cache.Dispose();
}
