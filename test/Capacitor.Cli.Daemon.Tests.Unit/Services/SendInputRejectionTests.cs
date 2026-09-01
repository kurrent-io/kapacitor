using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// What the sender learns when this daemon drops a dispatched input. The server's send returning
/// proves the transport wrote and nothing about what the agent received, so without these reports a
/// dropped message renders to whoever typed it as delivered.
/// </summary>
public class SendInputRejectionTests {
    static AgentOrchestrator Build(CaptureServerConnection server) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    [Test]
    public async Task An_agent_this_daemon_does_not_have_is_reported_as_unknown() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        var dispatchId = Guid.NewGuid();

        await orch.HandleSendInputForTest(new SendInputCommand("nobody", "hello", null, dispatchId));

        await Assert.That(server.InputRejections)
            .Contains((dispatchId, "nobody", AgentOrchestrator.SendInputDropReason.UnknownAgent));
    }

    /// <summary>A private agent ignores server-origin input by design; the sender is still owed the
    /// reason, which is the difference between "ignored" and "delivered".</summary>
    [Test]
    public async Task A_private_agent_is_reported_rather_than_silently_ignored() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        orch.SeedAgentForTest("private-1", status: "Running", isPrivate: true);
        var dispatchId = Guid.NewGuid();

        await orch.HandleSendInputForTest(new SendInputCommand("private-1", "hello", null, dispatchId));

        await Assert.That(server.InputRejections)
            .Contains((dispatchId, "private-1", AgentOrchestrator.SendInputDropReason.PrivateAgent));
    }

    /// <summary>A server that sends no dispatch id has nothing for a refusal to name, and predates
    /// the method that would receive one — it must see exactly the behaviour it always did.</summary>
    [Test]
    public async Task A_dispatch_with_no_id_reports_nothing() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);

        await orch.HandleSendInputForTest(new SendInputCommand("nobody", "hello", null));

        await Assert.That(server.InputRejections).IsEmpty();
    }
}
