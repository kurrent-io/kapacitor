using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// <see cref="AgentOrchestrator.DisposeAsync"/> must be idempotent and non-throwing: the DI
/// container tracks the singleton AND <c>DaemonRunner</c> disposes it explicitly, so it runs
/// twice by construction on every shutdown — the same structural double-teardown that crashed
/// <see cref="ServerConnection"/> (an ObjectDisposedException escaping into DI teardown aborts
/// a NativeAOT process). The orchestrator's <c>_shutdownCts</c> was also never disposed; adding
/// that Dispose without a run-once guard would recreate the crash exactly, so these contracts
/// pin body-ran-once AND cts-ends-cancelled-and-disposed durably.
///
/// Partial of <see cref="AgentOrchestratorVendorTests"/> to reuse its orchestrator builder and
/// server-connection capture.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task Orchestrator_dispose_twice_does_not_throw() {
        var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        await orch.DisposeAsync();
        // Pass 2 = the DI container's dispose walk re-entering after DaemonRunner's explicit call.
        await orch.DisposeAsync();
        // Reaching here without ObjectDisposedException is the assertion.
    }

    [Test]
    public async Task Orchestrator_second_dispose_does_not_reenter_the_body() {
        var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        await orch.DisposeAsync();
        await orch.DisposeAsync();

        // Durable run-once contract: removal of the guard fails this permanently — the naive
        // double-dispose test above passes vacuously against an unguarded-but-undisposing body.
        await Assert.That(orch.DisposeBodyRuns).IsEqualTo(1);
    }

    [Test]
    public async Task A_faulting_cancellation_callback_does_not_skip_child_teardown() {
        var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        var runtime = new FakeHostedAgentRuntime("claude", emitsTerminalOutput: false);
        orch.RegisterAgentForTest(new AgentInstance(
            "agent-cb", null, "", null, "/tmp", "claude", runtime,
            new WorktreeInfo("/tmp", "", "/tmp", IsStandalone: true), new CancellationTokenSource()));

        // A registered cancellation callback that throws faults CancelAsync's returned task
        // (AggregateException per the CTS contract) even though the cancel itself succeeded.
        // Uncontained, that fault would jump straight to the dispose finally — skipping the
        // processor drain and ALL child termination/cleanup — and the run-once guard means the
        // DI pass can never retry, stranding live child processes.
        orch.ShutdownCtsForTests.Token.Register(() => throw new InvalidOperationException("callback boom"));

        await orch.DisposeAsync(); // must not throw

        // Containment proof: teardown continued past the faulted cancel — the child was
        // terminated, and the finally still cancelled+disposed the CTS.
        await Assert.That(runtime.HasExited).IsTrue();
        await Assert.That(orch.ShutdownCtsForTests.IsCancellationRequested).IsTrue();
        await Assert.That(() => _ = orch.ShutdownCtsForTests.Token).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Orchestrator_dispose_cancels_and_disposes_its_shutdown_cts() {
        var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        var cts = orch.ShutdownCtsForTests;

        await orch.DisposeAsync();

        // Cancelled (IsCancellationRequested is safe to read post-dispose) AND disposed (the
        // Token property throws once the source is disposed) — removal of the Dispose call
        // fails this permanently.
        await Assert.That(cts.IsCancellationRequested).IsTrue();
        await Assert.That(() => _ = cts.Token).Throws<ObjectDisposedException>();

        // And a second pass over the now-disposed CTS must not throw.
        await orch.DisposeAsync();
    }
}
