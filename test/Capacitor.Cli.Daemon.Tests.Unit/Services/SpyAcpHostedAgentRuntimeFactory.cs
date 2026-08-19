using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> test double for the ACP seam: returns a
/// <see cref="HostedRuntimeStart"/> with <see cref="FakeAcpRuntime"/> threaded onto BOTH
/// <c>Runtime</c> and <c>Transcript</c> — mirroring <c>AcpHostedAgentRuntimeFactory</c>'s real
/// wiring, where the runtime IS its own transcript source.
/// </summary>
internal sealed class SpyAcpHostedAgentRuntimeFactory(string vendor = "cursor") : IHostedAgentRuntimeFactory {
    public string Vendor             { get; } = vendor;
    public bool   SupportsUnattended => false;

    /// <summary>Threaded onto the <see cref="FakeAcpRuntime"/> as its handshake-confirmed model —
    /// null stands in for a request that did not take (no match / agent rejected the option).</summary>
    public string? ResolvedModel { get; init; } = "gpt-x";

    /// <summary>§2.5: when true the produced runtime holds its first turn (source-claim path).</summary>
    public bool       DeferFirstTurn      { get; init; }
    /// <summary>Threaded onto the produced runtime to model a first-turn dispatch failure.</summary>
    public Exception? BeginFirstTurnThrow { get; init; }

    public int                  StartCalls  { get; private set; }
    public string?              LastAgentId { get; private set; }
    public RuntimeStartContext? LastContext { get; private set; }
    public FakeAcpRuntime?      LastRuntime { get; private set; }

    public bool IsAvailable() => true;

    public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        StartCalls++;
        LastAgentId = ctx.AgentId;
        LastContext = ctx;

        var runtime = new FakeAcpRuntime {
            ResolvedModel = ResolvedModel, DeferFirstTurn = DeferFirstTurn, BeginFirstTurnThrow = BeginFirstTurnThrow
        };
        LastRuntime = runtime;

        return Task.FromResult(new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime));
    }
}
