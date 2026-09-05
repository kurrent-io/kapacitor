using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class PermissionServiceTests {
    static PermissionPendingDto Dto(string id = "r1", string agent = "a1") =>
        new(id, agent, "s1", "claude", "Bash", null, null, false, false, "2026-08-28T10:00:00.0000000+00:00");

    sealed class FakePermissionStream {
        readonly Channel<PermissionStreamEvent?> _channel = Channel.CreateUnbounded<PermissionStreamEvent?>();
        int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public async IAsyncEnumerable<PermissionStreamEvent> RunAsync([EnumeratorCancellation] CancellationToken ct) {
            Interlocked.Increment(ref _attempts);
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct)) {
                if (evt is null) yield break;
                yield return evt;
            }
        }

        public void EmitSubscribed() => _channel.Writer.TryWrite(new PermissionStreamEvent.Subscribed());
        public void EmitPending(PermissionPendingDto dto) => _channel.Writer.TryWrite(new PermissionStreamEvent.Pending(dto));
        public void EmitResolved(string id, string source) => _channel.Writer.TryWrite(new PermissionStreamEvent.Resolved(new PermissionResolvedDto(id, "allow", source)));
        public void EndAttempt() => _channel.Writer.TryWrite(null);
    }

    sealed class Harness : IDisposable {
        public readonly FakeDaemonClientService Daemon = new();
        public readonly ScriptedLocalControlOps Ops = new();
        public readonly FakePermissionStream Stream = new();
        public readonly PermissionService Service;
        public readonly IObservableCache<PendingPermissionRequest, string> View;
        public IReadOnlySet<string> Agents = new HashSet<string>();
        public int Count;

        public Harness() {
            Service = new PermissionService(Daemon, Ops, Stream.RunAsync, new FakeTimeProvider(), CancellationToken.None);
            View = Service.Pending.AsObservableCache();
            Service.AgentsWithPending.Subscribe(s => Agents = s);
            Service.PendingCount.Subscribe(c => Count = c);
        }

        public void Connect(params string[] caps) => Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, caps));

        public async Task StartAsync() {
            Connect("consent/1", "permission/1");
            await WaitUntilAsync(() => Stream.Attempts == 1, what: "the subscribe attempt");
            Stream.EmitSubscribed();
        }

        public async Task<PendingPermissionRequest> EmitAsync(PermissionPendingDto dto) {
            Stream.EmitPending(dto);
            await WaitUntilAsync(() => View.Lookup(dto.RequestId).HasValue, what: $"pending {dto.RequestId} cached");
            return View.Lookup(dto.RequestId).Value;
        }

        public void Dispose() { Service.Dispose(); View.Dispose(); }
    }

    [Test]
    public async Task Subscribes_only_with_the_permission_capability_and_clears_on_a_down_level_daemon() {
        using var h = new Harness();
        h.Connect("consent/1");
        await Task.Delay(50);
        await Assert.That(h.Stream.Attempts).IsEqualTo(0);

        await h.StartAsync();
        await h.EmitAsync(Dto());
        await Assert.That(h.View.Count).IsEqualTo(1);

        h.Connect("consent/1");
        await WaitUntilAsync(() => h.View.Count == 0, what: "cleared on a down-level daemon");
    }

    [Test]
    public async Task Resolved_push_from_the_server_clears_entry_agent_set_and_count_together() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1", "a1"));
        await WaitUntilAsync(() => h.Agents.Contains("a1") && h.Count == 1, what: "derivations lit");

        h.Stream.EmitResolved("r1", "server");
        await WaitUntilAsync(() => h.View.Count == 0 && !h.Agents.Contains("a1") && h.Count == 0, what: "every derivation cleared");
    }

    [Test]
    public async Task A_replayed_ghost_of_a_resolved_request_is_dropped() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1"));
        h.Stream.EmitResolved("r1", "app");
        await WaitUntilAsync(() => h.View.Count == 0, what: "removed");
        h.Stream.EmitPending(Dto("r1"));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_outcomes_and_the_always_allow_payload() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto("r1"));

        h.Ops.QueuePermissionResolve(true);
        var applied = await h.Service.ResolveAsync(entry, PermissionAnswer.AllowAlways, CancellationToken.None);
        await Assert.That(applied.Kind).IsEqualTo(PermissionResolveKind.Applied);
        await Assert.That(h.Ops.PermissionResolvePayloads[0].Decision).IsEqualTo("allow");
        await Assert.That(h.Ops.PermissionResolvePayloads[0].ApplyPermissions!.Value.GetRawText()).IsEqualTo("""[{"type":"toolAlwaysAllow","tool":"Bash"}]""");
        await Assert.That(h.View.Count).IsEqualTo(0);

        var second = await h.EmitAsync(Dto("r2"));
        h.Ops.QueuePermissionResolve(false, "no pending permission request with that id");
        var already = await h.Service.ResolveAsync(second, PermissionAnswer.Deny, CancellationToken.None);
        await Assert.That(already.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        var third = await h.EmitAsync(Dto("r3"));
        h.Ops.QueuePermissionResolveFailure("daemon_unreachable");
        var failed = await h.Service.ResolveAsync(third, PermissionAnswer.Allow, CancellationToken.None);
        await Assert.That(failed.Kind).IsEqualTo(PermissionResolveKind.TransportFailure);
        await Assert.That(failed.Error).IsEqualTo("daemon_unreachable");
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Subscribed_clears_at_the_boundary_and_disconnect_retains() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1"));
        h.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(1);

        h.Connect("permission/1");
        await WaitUntilAsync(() => h.Stream.Attempts == 2, what: "resubscribe");
        await Assert.That(h.View.Count).IsEqualTo(1);
        h.Stream.EmitSubscribed();
        await WaitUntilAsync(() => h.View.Count == 0, what: "cleared at Subscribed");
    }

    static PermissionPendingDto PendingDto(string id, string agent, string vendor, string toolName, string? toolInputJson, bool omitted = false) {
        System.Text.Json.JsonElement? input = null;
        if (toolInputJson is not null) { using var d = System.Text.Json.JsonDocument.Parse(toolInputJson); input = d.RootElement.Clone(); }
        return new PermissionPendingDto(id, agent, "s1", vendor, toolName, input, null, omitted, false, "2026-08-28T10:00:00.0000000+00:00");
    }

    const string QuestionInput = """{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]}""";

    [Test]
    public async Task Classification_requires_claude_the_tool_name_present_input_and_a_parse() {
        using var h = new Harness();
        await h.StartAsync();
        var yes = await h.EmitAsync(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await Assert.That(yes.Questions).IsNotNull();
        var codex = await h.EmitAsync(PendingDto("q2", "a1", "codex", ClaudeElicitation.ToolName, QuestionInput));
        var wrongTool = await h.EmitAsync(PendingDto("q3", "a1", "claude", "Bash", QuestionInput));
        var omitted = await h.EmitAsync(PendingDto("q4", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput, omitted: true));
        var nullInput = await h.EmitAsync(PendingDto("q5", "a1", "claude", ClaudeElicitation.ToolName, null));
        var unparseable = await h.EmitAsync(PendingDto("q6", "a1", "claude", ClaudeElicitation.ToolName, """{"questions":[]}"""));
        foreach (var entry in new[] { codex, wrongTool, omitted, nullInput, unparseable })
            await Assert.That(entry.Questions).IsNull();
    }

    [Test]
    public async Task Answer_sends_allow_with_updated_input_and_concludes_on_either_ack() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));

        h.Ops.QueuePermissionResolve(true);
        var applied = await h.Service.AnswerAsync(entry, [new ElicitationAnswer("Pick", ["B"], null)], CancellationToken.None);
        await Assert.That(applied.Kind).IsEqualTo(PermissionResolveKind.Applied);
        var payload = h.Ops.PermissionResolvePayloads[0];
        await Assert.That(payload.Decision).IsEqualTo("allow");
        await Assert.That(payload.ApplyPermissions).IsNull();
        await Assert.That(payload.UpdatedInput!.Value.Prop("answers")!.Value.Str("Pick")).IsEqualTo("B");
        await Assert.That(h.View.Count).IsEqualTo(0);

        var second = await h.EmitAsync(PendingDto("q2", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        h.Ops.QueuePermissionResolve(false, "no pending permission request with that id");
        var already = await h.Service.AnswerAsync(second, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None);
        await Assert.That(already.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        var third = await h.EmitAsync(PendingDto("q3", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        h.Ops.QueuePermissionResolveFailure("daemon_unreachable");
        var failed = await h.Service.AnswerAsync(third, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None);
        await Assert.That(failed.Kind).IsEqualTo(PermissionResolveKind.TransportFailure);
        await Assert.That(h.View.Count).IsEqualTo(1);

        // A Resolved push after the failed send clears the survivor; a ghost replay stays dropped.
        h.Stream.EmitResolved("q3", "server");
        await WaitUntilAsync(() => h.View.Count == 0, what: "push cleared the survivor");
    }

    /// A withdraw carries no answer, and an older daemon that rejects the decision still gets the
    /// entry concluded here: the tool already ran, so the card is stale whatever the ack says.
    [Test]
    public async Task Withdraw_sends_withdraw_with_no_payload_and_concludes_on_either_ack() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto("r1"));

        h.Ops.QueuePermissionResolve(true);
        var applied = await h.Service.WithdrawAsync(entry, CancellationToken.None);
        await Assert.That(applied.Kind).IsEqualTo(PermissionResolveKind.Applied);
        var payload = h.Ops.PermissionResolvePayloads[0];
        await Assert.That(payload.Decision).IsEqualTo("withdraw");
        await Assert.That(payload.ApplyPermissions).IsNull();
        await Assert.That(payload.UpdatedInput).IsNull();
        await Assert.That(h.View.Count).IsEqualTo(0);

        var second = await h.EmitAsync(Dto("r2"));
        h.Ops.QueuePermissionResolve(false, "invalid resolve payload (decision must be allow|deny)");
        var rejected = await h.Service.WithdrawAsync(second, CancellationToken.None);
        await Assert.That(rejected.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        var third = await h.EmitAsync(Dto("r3"));
        h.Ops.QueuePermissionResolveFailure("daemon_unreachable");
        var failed = await h.Service.WithdrawAsync(third, CancellationToken.None);
        await Assert.That(failed.Kind).IsEqualTo(PermissionResolveKind.TransportFailure);
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Answer_rejects_an_unclassified_target_and_a_bad_answer_set_without_sending() {
        using var h = new Harness();
        await h.StartAsync();
        var plain = await h.EmitAsync(PendingDto("p1", "a1", "claude", "Bash", """{"command":"ls"}"""));
        await Assert.That(async () => await h.Service.AnswerAsync(plain, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None))
            .Throws<ArgumentException>();

        var entry = await h.EmitAsync(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await Assert.That(async () => await h.Service.AnswerAsync(entry, [], CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(h.Ops.PermissionResolveCalls).IsEqualTo(0);
        await Assert.That(h.View.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_resolved_push_landing_before_the_ack_ends_in_the_same_state() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));

        var gate = h.Ops.ArmPermissionResolve();
        var run = h.Service.AnswerAsync(entry, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None);
        h.Stream.EmitResolved("q1", "server");
        await WaitUntilAsync(() => h.View.Count == 0, what: "push evicted while the ack is in flight");
        gate.SetResult(new PermissionAckDto(false, "no pending permission request with that id"));
        var outcome = await run;
        await Assert.That(outcome.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        // The tombstoned id stays dead against a ghost replay.
        h.Stream.EmitPending(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Summary_seeds_and_stays_a_consistent_pair() {
        using var h = new Harness();
        var summaries = new List<PendingSummary>();
        using var sub = h.Service.Summary.Subscribe(summaries.Add);
        await Assert.That(summaries[0]).IsEqualTo(new PendingSummary(0, 0));

        await h.StartAsync();
        await h.EmitAsync(PendingDto("p1", "a1", "claude", "Bash", """{"command":"ls"}"""));
        await h.EmitAsync(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await WaitUntilAsync(() => summaries[^1] == new PendingSummary(1, 1), what: "one of each");

        h.Stream.EmitResolved("q1", "server");
        await WaitUntilAsync(() => summaries[^1] == new PendingSummary(1, 0), what: "question settled");
        foreach (var s in summaries) {
            await Assert.That(s.Permissions).IsGreaterThanOrEqualTo(0);
            await Assert.That(s.Questions).IsGreaterThanOrEqualTo(0);
        }
    }
}
