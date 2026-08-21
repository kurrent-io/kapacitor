using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>No-op <see cref="IHostedAgentRuntime"/> returned by <see cref="SpyHostedAgentRuntimeFactory"/>
/// so the orchestrator's post-launch RegisterAgentAsync/ReadAgentOutputAsync run against a
/// harmless stand-in instead of a real PTY or ACP connection. <see cref="ReadOutputAsync"/>
/// blocks until <see cref="ExitGate"/> is released (or <c>ct</c> cancels) rather than completing
/// immediately, mirroring the real ACP runtime's "stay open until the process exits" contract —
/// this lets Fix B/E tests observe orchestrator state (e.g. Status flips to "Running")
/// WHILE the agent is still live, before ever driving it to completion.</summary>
sealed class FakeHostedAgentRuntime(string vendor, bool emitsTerminalOutput) : IHostedAgentRuntime {
    public string Vendor              => vendor;
    public int    Pid                 => 0;
    public bool   HasExited           => ExitGate.Task.IsCompleted;
    public int?   ExitCode            => 0;
    public bool   EmitsTerminalOutput => emitsTerminalOutput;

    /// <summary>Released by a test to simulate the hosted process exiting.</summary>
    public TaskCompletionSource ExitGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    public ValueTask DisposeAsync() => default;
}
