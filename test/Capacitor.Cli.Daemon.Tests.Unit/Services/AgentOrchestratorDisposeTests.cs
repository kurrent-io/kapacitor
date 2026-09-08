using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The DI container and daemon runner both dispose the orchestrator, so teardown must be
/// idempotent and continue cleaning up children even when cancellation callbacks throw.
/// </summary>
public class AgentOrchestratorDisposeTests {
    [Test]
    public async Task Orchestrator_dispose_twice_does_not_throw() {
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        await orch.DisposeAsync();
        // Pass 2 = the DI container's dispose walk re-entering after DaemonRunner's explicit call.
        await orch.DisposeAsync();
        // Reaching here without ObjectDisposedException is the assertion.
    }

    [Test]
    public async Task Orchestrator_second_dispose_does_not_reenter_the_body() {
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
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
        using var tmp = new TempDir();
        var worktree = tmp.CreateDir("worktree");
        var sibling = tmp.CreateFile("unrelated.txt", "keep");
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        var runtime = new FakeHostedAgentRuntime("claude", emitsTerminalOutput: false);
        orch.RegisterAgentForTest(new AgentInstance(
            "agent-cb", null, "", null, worktree, "claude", runtime,
            new WorktreeInfo(worktree, "", worktree, IsStandalone: true), new CancellationTokenSource()));

        // A faulted cancellation must still drain the processor and clean up children;
        // the run-once guard prevents a later disposal from retrying skipped work.
        orch.ShutdownCtsForTests.Token.Register(() => throw new InvalidOperationException("callback boom"));

        await orch.DisposeAsync(); // must not throw

        await Assert.That(runtime.HasExited).IsTrue();
        await Assert.That(File.Exists(sibling)).IsTrue();
        await Assert.That(Directory.Exists(worktree)).IsFalse();
        await Assert.That(orch.ShutdownCtsForTests.IsCancellationRequested).IsTrue();
        await Assert.That(() => _ = orch.ShutdownCtsForTests.Token).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Orchestrator_dispose_cancels_and_disposes_its_shutdown_cts() {
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
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
