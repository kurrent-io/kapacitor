using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

/// <summary>PTY that has already exited and produces no output, so the read loop ends
/// immediately and FinalizeAgentRunAsync runs right after launch.</summary>
sealed class ImmediateExitPtyProcess : IPtyProcess {
    public int  Pid       => 4244;
    public bool HasExited => true;
    public int? ExitCode  => 0;

    public ValueTask DisposeAsync() => default;
    public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
    public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

#pragma warning disable CS1998
    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken _ = default) {
        yield break;
    }
#pragma warning restore CS1998

    public Task WriteAsync(string _) => Task.CompletedTask;
    public Task WriteAsync(byte[] _) => Task.CompletedTask;
    public void Resize(ushort     _, ushort __) { }
    public void SendInterrupt() { }
}
