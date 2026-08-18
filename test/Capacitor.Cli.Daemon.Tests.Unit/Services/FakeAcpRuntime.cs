using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="IHostedAgentRuntime"/> + <see cref="IAcpTranscriptSource"/> test double mirroring
/// the real <c>AcpHostedAgentRuntime</c>'s dual role (task 2) closely enough to exercise task 4's
/// wiring: <see cref="ReadOutputAsync"/> stays open until <see cref="TerminateAsync"/>/cancellation
/// (exactly like the real ACP runtime, and <see cref="FakeHostedAgentRuntime"/> above), and
/// <see cref="DisposeAsync"/> completes the <see cref="Envelopes"/> channel — mirroring the real
/// runtime's <c>DisposeAsync</c> completing its transcript channel — so the bounded final-drain
/// path has something real to observe.
/// </summary>
internal sealed class FakeAcpRuntime : IHostedAgentRuntime, IAcpTranscriptSource {
    readonly Channel<AcpEventEnvelope> _envelopes = Channel.CreateUnbounded<AcpEventEnvelope>();

    public string Vendor              => "cursor";
    public int    Pid                 => 0;
    public bool   HasExited           => ExitGate.Task.IsCompleted;
    public int?   ExitCode            => 0;
    public bool   EmitsTerminalOutput => false;

    public string  AcpSessionId  { get; init; } = "acp-sess-1";
    public string  Cwd           { get; init; } = "/tmp/acp-wt";
    public string? ResolvedModel { get; init; } = "gpt-x";

    public ChannelReader<AcpEventEnvelope> Envelopes       => _envelopes.Reader;
    public ChannelWriter<AcpEventEnvelope> EnvelopesWriter => _envelopes.Writer;

    /// <summary>Released by a test (via TerminateAsync, driven by HandleStopAgent) to simulate
    /// the ACP process exiting.</summary>
    public TaskCompletionSource ExitGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Number of times DisposeAsync ran — proves teardown actually disposed the
    /// runtime (and, since it's harmless to call twice, that CleanupAgentAsync's own later
    /// dispose call is a safe no-op-ish repeat, exactly like the real idempotent-guarded runtime).</summary>
    public int DisposeCount { get; private set; }

    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        var             ctTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg   = ct.Register(() => ctTcs.TrySetResult());
        await Task.WhenAny(ExitGate.Task, ctTcs.Task).ConfigureAwait(false);

        yield break;
    }

    public Task SendUserInputAsync(string  text) => Task.CompletedTask;
    public Task SendSpecialKeyAsync(string key) => Task.CompletedTask;
    public Task SendRawInputAsync(byte[]   data) => Task.CompletedTask;
    public void Resize(ushort              cols, ushort rows) { }
    public Task RequestGracefulStopAsync() => Task.CompletedTask;
    public Task WaitForExitAsync(TimeSpan?    timeout = null) => Task.CompletedTask;

    public Task TerminateAsync(TimeSpan? timeout = null) {
        ExitGate.TrySetResult();

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() {
        DisposeCount++;
        _envelopes.Writer.TryComplete(); // mirrors task 2's real DisposeAsync completing the transcript channel

        return default;
    }
}
