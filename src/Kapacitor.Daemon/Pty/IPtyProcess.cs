namespace kapacitor.Daemon.Pty;

public interface IPtyProcess : IAsyncDisposable
{
    int  Pid       { get; }
    bool HasExited { get; }
    int? ExitCode  { get; }
    IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default);
    Task WriteAsync(string input);
    Task WriteAsync(byte[] data);
    void Resize(ushort cols, ushort rows);
    void SendInterrupt();
    Task TerminateAsync(TimeSpan? timeout = null);
    Task WaitForExitAsync(TimeSpan? timeout = null);
}
