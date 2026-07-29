using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The vendor-neutral stop path's own diagnostics. <c>StopAgentCoreAsync</c> stops EVERY hosted
/// vendor, so its graceful-exit-timeout warning must name the vendor that actually failed to exit —
/// it used to say "claude" unconditionally, which mislabelled a Cursor (or Codex, or Copilot)
/// reviewer in the one log line an operator reads to work out which reviewer wedged.
///
/// Partial of <see cref="AgentOrchestratorVendorTests"/> to reuse its orchestrator builder,
/// server-connection capture, and no-op runtime double.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    sealed class CapturingOrchestratorLogger : ILogger<AgentOrchestrator> {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Test]
    [Arguments("cursor")]
    [Arguments("codex")]
    [Arguments("copilot")]
    public async Task Graceful_exit_timeout_warning_names_the_agents_own_vendor(string vendor) {
        var log = new CapturingOrchestratorLogger();
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), logger: log);

        // FakeHostedAgentRuntime reports HasExited == false until TerminateAsync runs and returns
        // from WaitForExitAsync immediately — i.e. exactly the "graceful window elapsed without the
        // CLI exiting" state, with no real 15s wait.
        orch.RegisterAgentForTest(new AgentInstance(
            $"agent-{vendor}", null, "", null, "/tmp", vendor,
            new FakeHostedAgentRuntime(vendor, emitsTerminalOutput: false),
            new WorktreeInfo("/tmp", "", "/tmp", IsStandalone: true), new CancellationTokenSource()));

        await orch.HandleStopAgent($"agent-{vendor}");

        var warning = log.Messages.SingleOrDefault(m => m.Contains("Graceful /exit window", StringComparison.Ordinal));
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!).Contains($"without {vendor} exiting");
        // The whole point: no other vendor's name may appear in this line.
        await Assert.That(warning).DoesNotContain("claude");
    }

    [Test]
    public async Task Graceful_exit_timeout_warning_still_names_claude_for_a_claude_agent() {
        // Guard against "fixing" the hardcoded name by removing the vendor from the message.
        var log = new CapturingOrchestratorLogger();
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), logger: log);

        orch.RegisterAgentForTest(new AgentInstance(
            "agent-claude", null, "", null, "/tmp", "claude",
            new FakeHostedAgentRuntime("claude", emitsTerminalOutput: false),
            new WorktreeInfo("/tmp", "", "/tmp", IsStandalone: true), new CancellationTokenSource()));

        await orch.HandleStopAgent("agent-claude");

        var warning = log.Messages.SingleOrDefault(m => m.Contains("Graceful /exit window", StringComparison.Ordinal));
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!).Contains("without claude exiting");
    }
}
