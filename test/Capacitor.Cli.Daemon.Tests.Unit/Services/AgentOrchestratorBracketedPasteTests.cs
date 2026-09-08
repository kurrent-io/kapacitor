using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Messages reach the PTY as a bracketed paste followed by separate submit keystrokes,
/// so a terminal still ingesting the paste can accept a later carriage return.
/// </summary>
public class AgentOrchestratorBracketedPasteTests {
    [TempDir] public required TempDir Worktree { get; init; }

    const string PasteStart = "\x1b[200~";
    const string PasteEnd   = "\x1b[201~";

    [Test]
    public async Task HandleSendInput_wraps_the_message_in_a_bracketed_paste_and_submits_with_repeated_Enter() {
        const string message = "line-1\nline-2\nline-3\nbig multi-line paste";

        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance(
            "agent-paste", null, "", null, Worktree.Path, "codex",
            new PtyHostedAgentRuntime("codex", pty, approvalsDisabled: true), new WorktreeInfo(Worktree.Path, "", Worktree.Path, IsStandalone: true), new CancellationTokenSource());
        orch.RegisterAgentForTest(agent);

        await orch.HandleSendInputForTest(new SendInputCommand("agent-paste", message, null));

        // The message is delivered as one bracketed-paste block, followed by the submitting
        // Enters as separate writes (one per SubmitCarriageReturnSchedule step) so at least one CR
        // lands as a distinct keypress after the TUI has finished ingesting the paste.
        var expectedCrs = PtyHostedAgentRuntime.SubmitCarriageReturnSchedule.Length;
        await Assert.That(pty.Writes.Count).IsEqualTo(1 + expectedCrs);
        await Assert.That(pty.Writes[0]).IsEqualTo($"{PasteStart}{message}{PasteEnd}");
        await Assert.That(pty.Writes.Skip(1)).IsEquivalentTo(Enumerable.Repeat("\r", expectedCrs));
    }

}
