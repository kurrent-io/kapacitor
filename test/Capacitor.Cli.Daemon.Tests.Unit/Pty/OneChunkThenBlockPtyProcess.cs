using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

/// <summary>
/// Emits one chunk (so the read loop calls SendTerminalOutputAsync) then keeps the
/// stream open by awaiting the read token — the loop parks in the blocked send until
/// the agent is stopped. HasExited is true so HandleStopAgent's graceful path is quick.
/// </summary>
sealed class OneChunkThenBlockPtyProcess : IPtyProcess {
    public int  Pid       => 4242;
    public bool HasExited => true;
    public int? ExitCode  => 0;

    public ValueTask DisposeAsync() => default;
    public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
    public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        yield return "x"u8.ToArray();

        await Task.Delay(Timeout.InfiniteTimeSpan, ct);

        yield break;
    }

    public Task WriteAsync(string _) => Task.CompletedTask;
    public Task WriteAsync(byte[] _) => Task.CompletedTask;
    public void Resize(ushort     _, ushort __) { }
    public void SendInterrupt() { }
}
