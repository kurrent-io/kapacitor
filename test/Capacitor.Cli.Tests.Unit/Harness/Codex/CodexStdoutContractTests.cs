using Capacitor.Cli.Commands.Harness;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

/// <summary>
/// Task 5: Codex's SessionStart hook must satisfy Codex's blocking stdout handshake
/// BEFORE any best-effort post-stdout work (watcher-ensure, global spool drain) runs — a
/// large/unreachable spool backlog must never stall the handshake. These tests exercise
/// <see cref="CodexHookCommand.RunSessionStartHandshakeForTest"/> directly, the seam
/// <c>HandleSessionStart</c> routes through in production.
/// </summary>
public class CodexStdoutContractTests {
    [Test]
    public async Task session_scoped_output_is_written_before_post_stdout_work() {
        var sw = new StringWriter();
        var order = new List<string>();

        // Seam: writeStdout records "stdout"; the post-stdout callback (standing in for the
        // watcher-ensure + spool drain) records "work".
        await CodexHookCommand.RunSessionStartHandshakeForTest(
            writeStdout: () => { order.Add("stdout"); sw.Write("""{"continue":true}"""); },
            postStdoutWork: () => { order.Add("work"); return Task.CompletedTask; });

        await Assert.That(order).IsEquivalentTo(["stdout", "work"]);
        await Assert.That(sw.ToString()).IsEqualTo("""{"continue":true}""");
    }

    [Test]
    public async Task stdout_write_is_not_delayed_by_a_stuck_post_stdout_task() {
        var sw = new StringWriter();
        var stdoutWritten = false;
        var gate = new TaskCompletionSource();

        // gate.Task never completes on its own — standing in for a large/unreachable spool
        // backlog (or a stuck drain) that can hang indefinitely.
        var handshake = CodexHookCommand.RunSessionStartHandshakeForTest(
            writeStdout: () => { stdoutWritten = true; sw.Write("""{"continue":true}"""); },
            postStdoutWork: () => gate.Task);

        // The overall handshake Task is still pending (it awaits postStdoutWork), but the
        // synchronous writeStdout callback must already have run and its output already be
        // observable — proving the stuck work behind it can never precede or gate the write.
        await Assert.That(stdoutWritten).IsTrue();
        await Assert.That(sw.ToString()).IsEqualTo("""{"continue":true}""");
        await Assert.That(handshake.IsCompleted).IsFalse();

        // Release the stuck work so the handshake Task can complete and the test can exit cleanly.
        gate.SetResult();
        await handshake;
    }
}
