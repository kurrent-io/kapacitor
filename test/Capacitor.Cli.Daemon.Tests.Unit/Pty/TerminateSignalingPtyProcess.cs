using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

/// <summary>
/// PTY that reports already-exited (so HandleStopAgent's graceful window is instant)
/// and signals when TerminateAsync runs. ReadOutputAsync blocks until ReadCts cancels,
/// keeping the read loop alive until the stop.
/// </summary>
sealed class TerminateSignalingPtyProcess(TaskCompletionSource terminated) : IPtyProcess {
    public int  Pid       => 4243;
    public bool HasExited => true;
    public int? ExitCode  => 0;

    public ValueTask DisposeAsync() => default;
    public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;

    public Task TerminateAsync(TimeSpan? _) {
        terminated.TrySetResult();

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) {
            /* released on stop */
        }

        yield break;
    }

    public Task WriteAsync(string _) => Task.CompletedTask;
    public Task WriteAsync(byte[] _) => Task.CompletedTask;
    public void Resize(ushort     _, ushort __) { }
    public void SendInterrupt() { }
}
