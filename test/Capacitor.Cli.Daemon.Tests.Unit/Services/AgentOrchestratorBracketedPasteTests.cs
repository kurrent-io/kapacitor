using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// a pasted (large, multi-line) message written to a hosted agent's PTY as raw bytes
/// followed by a carriage return often fails to submit — the CR races the still-ingesting
/// paste and is folded into it as a literal newline, so the text sits in the composer until a
/// later, isolated keystroke finishes it (Codex never submits at all; Claude ~50% of the time).
/// The fix delivers the message as a bracketed paste (ESC[200~ … ESC[201~) so the TUI treats it
/// as one block, then submits with the escalating carriage-return schedule (GitHub #349 — a
/// single CR after the paste is unreliably folded into paste-finalization; the extra CRs are
/// harmless empty-composer no-ops once submitted). This test drives
/// <c>AgentOrchestrator.HandleSendInput</c> end-to-end through the
/// <see cref="IHostedAgentRuntime"/> seam (<see cref="PtyHostedAgentRuntime"/> wrapping a fake
/// PTY) and asserts the wire shape reaching the PTY.
/// </summary>
public class AgentOrchestratorBracketedPasteTests {
    const string PasteStart = "\x1b[200~";
    const string PasteEnd   = "\x1b[201~";

    [Test]
    public async Task HandleSendInput_wraps_the_message_in_a_bracketed_paste_and_submits_with_repeated_Enter() {
        const string message = "line-1\nline-2\nline-3\nbig multi-line paste";

        var server = new CaptureServerConnection();
        var pty    = new RecordingPtyProcess();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance(
            "agent-paste", null, "", null, "/tmp", "codex",
            new PtyHostedAgentRuntime("codex", pty, approvalsDisabled: true), new WorktreeInfo("/tmp", "", "/tmp", IsStandalone: true), new CancellationTokenSource());
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
