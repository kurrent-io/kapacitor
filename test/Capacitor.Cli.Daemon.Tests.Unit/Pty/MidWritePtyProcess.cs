using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

/// <summary>PTY double that runs a callback inside every write, standing in for something the
/// agent does before the delivery returns to its caller — a turn completing during the submit
/// spray, a hook relay landing mid-write. Produces no output.</summary>
internal sealed class MidWritePtyProcess(Action onWrite) : IPtyProcess {
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

    public Task WriteAsync(string input) {
        onWrite();

        return Task.CompletedTask;
    }

    public Task WriteAsync(byte[] data) {
        onWrite();

        return Task.CompletedTask;
    }

    public void Resize(ushort _, ushort __) { }
    public void SendInterrupt() { }
}
