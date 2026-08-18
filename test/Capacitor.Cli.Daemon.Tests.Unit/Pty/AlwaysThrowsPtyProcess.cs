using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

/// <summary>PTY double whose every write throws — simulates a dead/closed PTY
/// (<see cref="IPtyProcess.WriteAsync(string)"/> is documented "unguarded and throws on a closed
/// pipe" — see <c>PtyHostedAgentRuntime.WriteSubmitCarriageReturnAsync</c>'s remarks), so
/// <c>AgentOrchestrator.HandleSendInput</c>'s delivery await never completes without an
/// exception and the activity-clock advance it gates on must not run.</summary>
internal sealed class AlwaysThrowsPtyProcess : IPtyProcess {
    public int  Pid       => 5151;
    public bool HasExited => false;
    public int? ExitCode  => null;

    public ValueTask DisposeAsync() => default;
    public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
    public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

#pragma warning disable CS1998
    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken _ = default) {
        yield break;
    }
#pragma warning restore CS1998

    public Task WriteAsync(string _) => throw new IOException("simulated closed pty pipe");
    public Task WriteAsync(byte[] _) => throw new IOException("simulated closed pty pipe");

    public void Resize(ushort _, ushort __) { }
    public void SendInterrupt() { }
}
