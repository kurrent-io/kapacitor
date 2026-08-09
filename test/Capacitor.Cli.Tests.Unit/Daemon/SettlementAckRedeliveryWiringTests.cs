using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

// Settlement lost-ack redelivery (D1) (Task 7 wiring): the orchestrator re-delivers unretired terminal acks on a status-report
// tick AND on reconnect (re-registration). The freeze + re-delivery MECHANISM is pinned at the processor
// level (SequencedCommandProcessorTests); these pin that the orchestrator actually invokes it end-to-end
// — processor -> _server.CommandAckAsync. Reuses the AgentOrchestratorVendorTests harness
// (BuildOrchestrator / SpyPtyProcessFactory / SeqCaptureServerConnection / WaitBoundedAsync).
public partial class AgentOrchestratorVendorTests {
    // A published processor with exactly one SETTLED (Processed, unretired) sequenced command, so the
    // server has captured its single proactive terminal ack and a re-delivery has something to re-send.
    static async Task<(AgentOrchestrator Orch, SeqCaptureServerConnection Server, int ProactiveAcks)>
        OrchestratorWithOneSettledCommandAsync() {
        var server = new SeqCaptureServerConnection();
        var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        orch.PublishSequencedProcessorForTest();
        await orch.ProcessorForTest!.SubmitAsync(
            new SequencedItem(SequencedKind.Launch, orch.DaemonEpochForTest, 1, "cmd1", "a1"),
            () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "a1", "sess")));
        await WaitBoundedAsync(orch.DrainLaneForTest(), "the lane never settled the command");
        return (orch, server, server.Acks.Count);
    }

    [Test]
    public async Task Status_report_tick_redelivers_unretired_terminal_acks() {
        var (orch, server, proactive) = await OrchestratorWithOneSettledCommandAsync();
        await using var _ = orch;
        await Assert.That(proactive).IsGreaterThanOrEqualTo(1);

        await orch.SendDaemonStatusReportOnceAsync();       // the periodic / on-request tick

        await Assert.That(server.Acks.Count).IsEqualTo(proactive + 1);
        await Assert.That(server.Acks[^1].Seq).IsEqualTo(1L);
        await Assert.That(server.Acks[^1].State).IsEqualTo(CommandAckState.Processed);
    }

    [Test]
    public async Task Reconnect_reregistration_redelivers_unretired_terminal_acks() {
        var (orch, server, proactive) = await OrchestratorWithOneSettledCommandAsync();
        await using var _ = orch;

        await orch.ReRegisterAgentsAsync();                 // the reconnect hook (no agents to re-register)

        await Assert.That(server.Acks.Count).IsEqualTo(proactive + 1);
        await Assert.That(server.Acks[^1].Seq).IsEqualTo(1L);
        await Assert.That(server.Acks[^1].State).IsEqualTo(CommandAckState.Processed);
    }
}
