using System.Net.Sockets;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// One accepted connection, scripted: records every inbound frame, sends the
/// queued replies when told to. Socket path must come from
/// tmp.GetResolvedPath(...) — sockaddr_un budget.
sealed class ScriptedAttachServer : IAsyncDisposable {
    readonly Socket _listener;
    Socket? _conn;
    NetworkStream? _stream;
    public readonly List<LocalFrame> Received = [];
    public readonly TaskCompletionSource<LocalFrame> FirstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Path { get; }

    public ScriptedAttachServer(string socketPath) {
        Path = socketPath;
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(1);
    }

    public async Task AcceptAndPumpInboundAsync(CancellationToken ct = default) {
        _conn = await _listener.AcceptAsync(ct);
        _stream = new NetworkStream(_conn, ownsSocket: false);
        _ = Task.Run(async () => {
            try {
                while (await FrameCodec.ReadAsync(_stream, ct) is { } f) {
                    lock (Received) Received.Add(f);
                    FirstFrame.TrySetResult(f);
                }
            } catch { /* connection closed by client — fine for a script */ }
        }, ct);
    }

    public Task SendAsync(LocalFrame frame) => FrameCodec.WriteAsync(_stream!, frame, CancellationToken.None);
    public Task SendAttachedAsync(string agentId, byte[] snapshot) => SendAsync(FrameCodec.Attached(agentId, snapshot));
    public Task SendAttachedReadOnlyAsync(string agentId, string reason, byte[] snapshot) => SendAsync(FrameCodec.AttachedReadOnly(agentId, reason, snapshot));
    public Task SendStdoutAsync(byte[] bytes) => SendAsync(LocalFrame.Stdout(bytes));
    public Task SendExitedAsync(int code) => SendAsync(LocalFrame.Exited(code));
    public Task SendErrorAsync(string text) => SendAsync(new LocalFrame(FrameType.Error) { Text = text });
    public void CloseConnection() { _stream?.Dispose(); _conn?.Dispose(); }
    /// For truncation tests: write raw bytes (a partial header/payload), then close.
    public async Task SendRawThenCloseAsync(byte[] raw) { await _stream!.WriteAsync(raw); CloseConnection(); }

    public async ValueTask DisposeAsync() { CloseConnection(); _listener.Dispose(); await Task.CompletedTask; }
}
