using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Test double for the Task 10 runtime-selection seam: records that <see cref="StartAsync"/>
/// was called (and with what agent id) and returns a no-op <see cref="FakeHostedAgentRuntime"/>,
/// without ever spawning a real process — proves the orchestrator routes a launch to the correct
/// <see cref="IHostedAgentRuntimeFactory"/> by vendor.
/// </summary>
sealed class SpyHostedAgentRuntimeFactory(string vendor) : IHostedAgentRuntimeFactory {
    public string Vendor                                    { get; } = vendor;
    public bool   SupportsUnattended                        { get; init; }
    public bool   SupportsBorrowedReviewFlow                { get; init; }
    public bool   BorrowedReviewRequiresIndependentSnapshot { get; init; }

    /// <summary>Stands in for a runtime that isolates HOME on every review it serves (Antigravity
    /// today). Independent of borrowed-ness on purpose — that is the whole point of the
    /// declaration, and a test that could only set it alongside a borrow would prove nothing.</summary>
    public bool   ReviewFlowRedirectsHome { get; init; }

    /// <summary>Task 8: lets a test give this factory a reviewer-model resolver so the
    /// orchestrator's ResolveReviewerModel preflight handler can resolve against it.</summary>
    public IReviewerModelResolver? ReviewerModelResolver { get; init; }

    /// <summary>Defaults to <c>true</c>, matching the interface default and every pre-existing
    /// runtime. A test sets <c>false</c> to stand in for a vendor whose model-selection hook is a
    /// no-op (Kiro today), exercising <see cref="ModelSelectionLaunchPolicy"/>.</summary>
    public bool SupportsModelSelection { get; init; } = true;

    /// <summary>Threaded onto the <see cref="FakeHostedAgentRuntime"/> this factory returns —
    /// defaults to <c>false</c> (ACP-shaped) matching this factory's original "cursor" use, but
    /// settable so a test can exercise the PTY-shaped (<c>true</c>) lifecycle branch too.</summary>
    public bool EmitsTerminalOutput { get; init; }

    public int                  StartCalls  { get; private set; }
    public string?              LastAgentId { get; private set; }
    public RuntimeStartContext? LastContext { get; private set; }
    public Exception?           StartThrow  { get; init; }

    public FakeHostedAgentRuntime? LastRuntime { get; private set; }

    public bool IsAvailable() => true;

    public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        StartCalls++;
        LastAgentId = ctx.AgentId;
        LastContext = ctx;
        if (StartThrow is not null) throw StartThrow;

        var runtime = new FakeHostedAgentRuntime(Vendor, EmitsTerminalOutput);
        LastRuntime = runtime;

        return Task.FromResult(new HostedRuntimeStart(runtime, McpConfigPath: null));
    }
}
