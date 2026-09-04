using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Tests.Unit.WorkItems;

/// The fetch composition, over a scripted channel: which call's status decides the read's kind, and
/// which failures only degrade a section.
public class WorkContextReaderTests {
    sealed class ScriptedChannel : IWorkContextChannel {
        public WorkContextOutcome<List<SessionWorkItemAssignmentDto>> Assignments = new(200, [], null);
        public WorkContextOutcome<WorkItemTopologyDto> Topology = new(200, new WorkItemTopologyDto(), null);
        public WorkContextOutcome<SessionSummaryDto> Summary = new(200, new SessionSummaryDto { SessionId = "s1" }, null);
        public readonly List<string> Calls = [];
        public TaskCompletionSource SummaryGate = new();
        public bool GateSummary;

        public Task<WorkContextOutcome<List<SessionWorkItemAssignmentDto>>> GetSessionAssignmentsAsync(string sessionId, CancellationToken ct) {
            Calls.Add($"assignments:{sessionId}");
            return Task.FromResult(Assignments);
        }

        public Task<WorkContextOutcome<WorkItemTopologyDto>> GetTopologyAsync(string workItemId, CancellationToken ct) {
            Calls.Add($"topology:{workItemId}");
            return Task.FromResult(Topology);
        }

        public async Task<WorkContextOutcome<SessionSummaryDto>> GetSessionSummaryAsync(string sessionId, CancellationToken ct) {
            Calls.Add($"summary:{sessionId}");
            if (GateSummary) await SummaryGate.Task;
            return Summary;
        }
    }

    static SessionWorkItemAssignmentDto Row(string id, bool primary = false, string label = "WK-1 — Title") =>
        new() { WorkItemId = id, Label = label, Source = "mcp", Confidence = 1, IsPrimary = primary };

    static WorkContextOutcome<T> PlanGate<T>() where T : class =>
        new(403, null, new WorkItemErrorDto { Error = WorkContextReader.PlanGateError, Message = "Upgrade." });

    static Task<WorkContextRead> Read(ScriptedChannel channel) =>
        WorkContextReader.ReadAsync(channel, "s1", CancellationToken.None);

    [Test]
    public async Task Ready_carries_the_primary_its_topology_and_the_summary() {
        var channel = new ScriptedChannel { Assignments = new(200, [Row("w2"), Row("w1", primary: true)], null) };

        var read = await Read(channel);

        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Ready);
        await Assert.That(read.Primary!.WorkItemId).IsEqualTo("w1");
        await Assert.That(read.Topology).IsNotNull();
        await Assert.That(read.Summary!.SessionId).IsEqualTo("s1");
        await Assert.That(read.TopologyFailed).IsFalse();
        await Assert.That(read.SummaryFailed).IsFalse();
        await Assert.That(channel.Calls).Contains("topology:w1");
    }

    [Test]
    public async Task Without_a_primary_flag_the_first_row_is_primary() {
        var channel = new ScriptedChannel { Assignments = new(200, [Row("w3"), Row("w4")], null) };
        var read = await Read(channel);
        await Assert.That(read.Primary!.WorkItemId).IsEqualTo("w3");
    }

    [Test]
    public async Task No_assignments_is_ready_with_a_null_primary_and_the_summary_still_carried() {
        var read = await Read(new ScriptedChannel());

        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Ready);
        await Assert.That(read.Primary).IsNull();
        await Assert.That(read.Topology).IsNull();
        await Assert.That(read.Summary).IsNotNull();
    }

    [Test]
    public async Task A_2xx_assignments_response_with_no_body_is_unreachable() {
        var channel = new ScriptedChannel { Assignments = new(200, null, null) };
        var read = await Read(channel);
        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Unreachable);
        await Assert.That(read.Detail).IsEqualTo("malformed response");
    }

    [Test]
    [Arguments("assignments")]
    [Arguments("topology")]
    [Arguments("summary")]
    public async Task A_final_401_on_any_call_signs_the_read_out(string call) {
        var channel = new ScriptedChannel { Assignments = new(200, [Row("w1", primary: true)], null) };
        switch (call) {
            case "assignments": channel.Assignments = new(401, null, null); break;
            case "topology":    channel.Topology = new(401, null, null); break;
            case "summary":     channel.Summary = new(401, null, null); break;
        }

        var read = await Read(channel);

        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
    }

    [Test]
    public async Task A_403_with_the_plan_code_on_assignments_is_not_in_plan() {
        var channel = new ScriptedChannel { Assignments = PlanGate<List<SessionWorkItemAssignmentDto>>() };
        var read = await Read(channel);
        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.NotInPlan);
        await Assert.That(read.Detail).IsEqualTo("Upgrade.");
    }

    [Test]
    public async Task A_403_with_the_plan_code_on_topology_is_not_in_plan_too() {
        var channel = new ScriptedChannel { Assignments = new(200, [Row("w1", primary: true)], null), Topology = PlanGate<WorkItemTopologyDto>() };
        var read = await Read(channel);
        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.NotInPlan);
    }

    [Test]
    public async Task A_403_with_another_code_or_no_body_is_unreachable() {
        var other = new ScriptedChannel { Assignments = new(403, null, new WorkItemErrorDto { Error = "forbidden" }) };
        var bodyless = new ScriptedChannel { Assignments = new(403, null, null) };

        await Assert.That((await Read(other)).Kind).IsEqualTo(WorkContextReadKind.Unreachable);
        await Assert.That((await Read(bodyless)).Kind).IsEqualTo(WorkContextReadKind.Unreachable);
    }

    [Test]
    public async Task A_404_is_session_unknown_and_status_zero_is_unreachable() {
        await Assert.That((await Read(new ScriptedChannel { Assignments = new(404, null, null) })).Kind).IsEqualTo(WorkContextReadKind.SessionUnknown);
        var zero = await Read(new ScriptedChannel { Assignments = new(0, null, null) });
        await Assert.That(zero.Kind).IsEqualTo(WorkContextReadKind.Unreachable);
        await Assert.That(zero.Detail).IsEqualTo("no response");
    }

    [Test]
    public async Task Topology_failures_degrade_the_section() {
        var non2xx = new ScriptedChannel { Assignments = new(200, [Row("w1", primary: true)], null), Topology = new(500, null, null) };
        var noBody = new ScriptedChannel { Assignments = new(200, [Row("w1", primary: true)], null), Topology = new(200, null, null) };

        foreach (var channel in new[] { non2xx, noBody }) {
            var read = await Read(channel);
            await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Ready);
            await Assert.That(read.Topology).IsNull();
            await Assert.That(read.TopologyFailed).IsTrue();
        }
    }

    [Test]
    public async Task Summary_failures_degrade_the_section() {
        var non2xx = new ScriptedChannel { Summary = new(404, null, null) };
        var noBody = new ScriptedChannel { Summary = new(200, null, null) };

        foreach (var channel in new[] { non2xx, noBody }) {
            var read = await Read(channel);
            await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Ready);
            await Assert.That(read.Summary).IsNull();
            await Assert.That(read.SummaryFailed).IsTrue();
        }
    }

    [Test]
    public async Task The_summary_task_is_awaited_even_when_assignments_end_the_read_early() {
        var channel = new ScriptedChannel { Assignments = new(404, null, null), GateSummary = true };
        var pending = Read(channel);

        await Task.Delay(50);
        await Assert.That(pending.IsCompleted).IsFalse();
        channel.SummaryGate.SetResult();

        await Assert.That((await pending).Kind).IsEqualTo(WorkContextReadKind.SessionUnknown);
    }
}
