using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

public class PtyHostedAgentRuntimeTests {
    // Records every write so we can assert the split-write ordering.
    sealed class RecordingPty : IPtyProcess {
        public List<string> StringWrites { get; } = [];
        public List<byte[]>  ByteWrites   { get; } = [];
        public (ushort Cols, ushort Rows)? LastResize { get; private set; }

        public int  Pid       => 4321;
        public bool HasExited => false;
        public int? ExitCode  => null;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   timeout = null) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string input) { StringWrites.Add(input); return Task.CompletedTask; }
        public Task WriteAsync(byte[] data)  { ByteWrites.Add(data);    return Task.CompletedTask; }
        public void Resize(ushort cols, ushort rows) => LastResize = (cols, rows);
        public void SendInterrupt() { }
    }

    [Test]
    public async Task SendUserInput_wraps_text_in_a_bracketed_paste_then_carriage_return() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty, approvalsDisabled: true);

        await runtime.SendUserInputAsync("hello world");

        // Approvals disabled → one bracketed-paste block then the CR spray (one CR per
        // SubmitCarriageReturnSchedule step) so at least one lands past codex's Enter-suppression
        // window (GitHub #349).
        var expectedCrs = PtyHostedAgentRuntime.SubmitCarriageReturnSchedule.Length;
        await Assert.That(pty.StringWrites.Count).IsEqualTo(1 + expectedCrs);
        await Assert.That(pty.StringWrites[0]).IsEqualTo("\x1b[200~hello world\x1b[201~");
        await Assert.That(pty.StringWrites.Skip(1)).IsEquivalentTo(Enumerable.Repeat("\r", expectedCrs));
    }

    [Test]
    public async Task RequestGracefulStop_writes_exit_then_carriage_return() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty, approvalsDisabled: true);

        await runtime.RequestGracefulStopAsync();

        // "/exit" submits via the same CR spray (a single CR failed to submit it — the
        // SIGTERM-fallback half of GitHub #349).
        var expectedCrs = PtyHostedAgentRuntime.SubmitCarriageReturnSchedule.Length;
        await Assert.That(pty.StringWrites.Count).IsEqualTo(1 + expectedCrs);
        await Assert.That(pty.StringWrites[0]).IsEqualTo("/exit");
        await Assert.That(pty.StringWrites.Skip(1)).IsEquivalentTo(Enumerable.Repeat("\r", expectedCrs));
    }

    [Test]
    public async Task SendSpecialKey_translates_via_SpecialKeyMap() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty);

        // SpecialKeyMap.ToBytes recognizes PascalCase keys: Escape/Tab/Enter/CtrlC/ArrowUp/
        // ArrowDown/ShiftTab. "Enter" maps to [0x0d].
        await runtime.SendSpecialKeyAsync("Enter");

        await Assert.That(pty.ByteWrites.Count).IsEqualTo(1);
        await Assert.That(pty.ByteWrites[0].Length).IsEqualTo(1);
        await Assert.That(pty.ByteWrites[0][0]).IsEqualTo((byte)0x0d);
    }

    [Test]
    public async Task SendSpecialKey_unknown_key_writes_nothing() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty);

        await runtime.SendSpecialKeyAsync("definitely-not-a-key");

        await Assert.That(pty.ByteWrites.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SendRawInput_writes_bytes_verbatim() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("claude", pty);
        var data    = new byte[] { 1, 2, 3 };

        await runtime.SendRawInputAsync(data);

        await Assert.That(pty.ByteWrites.Count).IsEqualTo(1);
        await Assert.That(pty.ByteWrites[0].Length).IsEqualTo(data.Length);
        await Assert.That(pty.ByteWrites[0][0]).IsEqualTo(data[0]);
        await Assert.That(pty.ByteWrites[0][1]).IsEqualTo(data[1]);
        await Assert.That(pty.ByteWrites[0][2]).IsEqualTo(data[2]);
    }

    [Test]
    public async Task Resize_forwards_to_pty() {
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("codex", pty);

        runtime.Resize(100, 30);

        await Assert.That(pty.LastResize).IsEqualTo(((ushort)100, (ushort)30));
    }

    [Test]
    public async Task Vendor_and_pid_are_exposed_from_the_wrapped_pty() {
        var runtime = new PtyHostedAgentRuntime("codex", new RecordingPty());

        await Assert.That(runtime.Vendor).IsEqualTo("codex");
        await Assert.That(runtime.Pid).IsEqualTo(4321);
    }

    [Test]
    public async Task SubmitAsync_stops_sending_carriage_returns_once_the_process_has_exited() {
        // The ~2.4s submit schedule can outlive the reviewer: HasExited becomes true right after
        // the paste write (before any CR) → the guard must short-circuit, sending zero CRs.
        var pty     = new ClosablePty(exitedAfter: writes => writes >= 1);
        var runtime = new PtyHostedAgentRuntime("codex", pty, approvalsDisabled: true);

        await runtime.SendUserInputAsync("hi");

        await Assert.That(pty.Writes.Count).IsEqualTo(1);                   // just the paste, no CRs
        await Assert.That(pty.Writes[0].StartsWith("\x1b[200~", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SubmitAsync_swallows_a_pipe_closed_write_and_stops() {
        // The race where HasExited hasn't flipped yet but the reviewer's pipe is already closing:
        // the CR write throws a pipe-closed IOException. SendUserInputAsync must NOT propagate it
        // (a benign post-exit write) and must send no further CRs (GitHub #349, Qodo finding #2).
        var pty     = new ClosablePty(throwOnCrIndex: 0);
        var runtime = new PtyHostedAgentRuntime("codex", pty, approvalsDisabled: true);

        await runtime.SendUserInputAsync("hi");   // must complete without throwing

        await Assert.That(pty.Writes.Count).IsEqualTo(2);                   // paste + the CR that threw
        await Assert.That(pty.Writes[1]).IsEqualTo("\r");
    }

    [Test]
    public async Task SendUserInput_sends_a_single_CR_when_approvals_are_enabled() {
        // Interactive session (approvals enabled → a prompt is possible): submit with ONE CR so a
        // stray Enter can't accept a live approval dialog — never the multi-CR spray (Qodo review).
        var pty     = new RecordingPty();
        var runtime = new PtyHostedAgentRuntime("codex", pty, approvalsDisabled: false);

        await runtime.SendUserInputAsync("hello");

        await Assert.That(pty.StringWrites.Count).IsEqualTo(2);   // paste + exactly one CR
        await Assert.That(pty.StringWrites[0]).IsEqualTo("\x1b[200~hello\x1b[201~");
        await Assert.That(pty.StringWrites[1]).IsEqualTo("\r");
    }

    // Fake PTY that can report HasExited as a function of writes-so-far, and/or throw a
    // pipe-closed IOException on a chosen carriage-return write — to exercise SubmitAsync's
    // mid-schedule exit handling.
    sealed class ClosablePty(Func<int, bool>? exitedAfter = null, int? throwOnCrIndex = null) : IPtyProcess {
        readonly Func<int, bool> _exitedAfter = exitedAfter ?? (_ => false);
        int _crCount;

        public List<string> Writes { get; } = [];

        public int  Pid       => 9;
        public bool HasExited => _exitedAfter(Writes.Count);
        public int? ExitCode  => null;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   timeout = null) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string input) {
            Writes.Add(input);
            if (input == "\r" && throwOnCrIndex == _crCount++)
                throw new System.IO.IOException("The pipe is being closed.");
            return Task.CompletedTask;
        }

        public Task WriteAsync(byte[] data) { Writes.Add(System.Text.Encoding.UTF8.GetString(data)); return Task.CompletedTask; }
        public void Resize(ushort cols, ushort rows) { }
        public void SendInterrupt() { }
    }
}
