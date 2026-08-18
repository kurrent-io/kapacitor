using System.Runtime.CompilerServices;
using System.Text;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

/// <summary>PTY double that records the ordered sequence of writes and produces no output.</summary>
internal sealed class RecordingPtyProcess : IPtyProcess {
    readonly List<string> _writes = [];
    readonly Lock         _gate   = new();

    public IReadOnlyList<string> Writes {
        get { lock (_gate) { return [.. _writes]; } }
    }

    public int  Pid       => 5150;
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
        lock (_gate) { _writes.Add(input); }

        return Task.CompletedTask;
    }

    public Task WriteAsync(byte[] data) {
        lock (_gate) { _writes.Add(Encoding.UTF8.GetString(data)); }

        return Task.CompletedTask;
    }

    public void Resize(ushort _, ushort __) { }
    public void SendInterrupt() { }
}
