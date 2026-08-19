using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// §2.5: the orchestrator's deferred-first-turn SOURCE-CLAIM sequence — the daemon driver for a
/// runtime that HOLDS its first turn (<c>RequiresSourceClaimBeforeFirstTurn</c>). The load-bearing
/// facts under test: the claim SUPERSEDES <c>AcpSessionStarted</c> (no bind call), the forwarder starts
/// without re-binding, the held first turn dispatches strictly after the claim, the launch is confirmed,
/// and a Rejected / method-not-found claim tears the agent down (never a first turn) while a confirm
/// failure never does. All fire-and-forget from the launch, asserted through the deterministic
/// <c>AcpCallOrder</c> / signal seams (never Task.Delay).
/// </summary>
public class AgentOrchestratorSourceClaimTests {
    static SpyAcpHostedAgentRuntimeFactory DeferredFactory(Exception? beginFirstTurnThrow = null) =>
        new(vendor: "cursor") { DeferFirstTurn = true, BeginFirstTurnThrow = beginFirstTurnThrow };

    [Test]
    public async Task Deferred_launch_claims_then_forwards_without_rebinding_then_first_turns_then_confirms() {
        var (repoPath, cleanup) = GitRepoHarness.CreateGitRepo();
        try {
            var server  = new CaptureServerConnection();
            var factory = DeferredFactory();

            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
                server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);
            orch.AcpFinalDrainBudget = TimeSpan.FromMilliseconds(200);

            await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-sc", repoPath));

            // The confirm is the LAST step of the sequence, so awaiting it proves the whole chain ran.
            var confirmedToken = await server.ConfirmSignal.Reader.ReadAsync().AsTask().WaitAsync(WaitHarness.AcpHangGuard);
            await Assert.That(confirmedToken).IsEqualTo(1);

            // The claim SUPERSEDES AcpSessionStarted — a source claim ran, an AcpSessionStarted bind did NOT.
            await Assert.That(server.AcpCallOrder).Contains("sourceClaim:agent-sc");
            await Assert.That(server.AcpCallOrder.Any(e => e.StartsWith("bind:", StringComparison.Ordinal))).IsFalse();
            await Assert.That(server.AcpSessionStartedCalls).IsEmpty();

            // Ordering: register < sourceClaim, and the forwarder registered its binding + sent events.
            var registerIndex = server.AcpCallOrder.IndexOf("register:agent-sc");
            var claimIndex    = server.AcpCallOrder.IndexOf("sourceClaim:agent-sc");
            await Assert.That(registerIndex).IsGreaterThanOrEqualTo(0);
            await Assert.That(claimIndex).IsGreaterThan(registerIndex);

            // The held first turn was dispatched exactly once, and the confirm carried the claim's token.
            await Assert.That(factory.LastRuntime!.BeginFirstTurnCalls).IsEqualTo(1);
            await Assert.That(server.ConfirmTokens).Contains(1L);

            await orch.HandleStopAgentForTest("agent-sc");
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Rejected_claim_tears_the_launch_down_without_a_first_turn_or_confirm() {
        var (repoPath, cleanup) = GitRepoHarness.CreateGitRepo();
        try {
            var launchFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new CaptureServerConnection {
                SourceClaimOutcome  = new AcpSourceClaimOutcome(AcpBindOutcome.Rejected, 0, 0),
                LaunchFailedEntered = launchFailed
            };
            var factory = DeferredFactory();

            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
                server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);
            orch.AcpFinalDrainBudget = TimeSpan.FromMilliseconds(200);

            await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-rej", repoPath));

            // FailEnvelopeSourcedLaunchAsync runs CleanupAgentAsync THEN LaunchFailedAsync, so awaiting the
            // LaunchFailed entry proves the teardown (dispose + unregister) already completed.
            await launchFailed.Task.WaitAsync(WaitHarness.AcpHangGuard);

            await Assert.That(server.LaunchFailedCalls.Select(c => c.AgentId)).Contains("agent-rej");
            await Assert.That(server.AgentUnregisteredCalls).Contains("agent-rej"); // CleanupAgentAsync ran
            await Assert.That(factory.LastRuntime!.DisposeCount).IsGreaterThan(0);  // the child was stopped
            await Assert.That(factory.LastRuntime!.BeginFirstTurnCalls).IsEqualTo(0); // never dispatched a turn
            await Assert.That(server.ConfirmTokens).IsEmpty();                        // never confirmed
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Method_not_found_claim_is_a_coded_launch_failure_with_teardown() {
        var (repoPath, cleanup) = GitRepoHarness.CreateGitRepo();
        try {
            var launchFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new CaptureServerConnection {
                // Models a pre-source-claim server: the hub method doesn't exist. SignalR raises a
                // HubException for this — NOT a transient disconnect (ConnectionRetry propagates it),
                // unlike an InvalidOperationException which it would retry forever.
                SourceClaimThrow    = new Microsoft.AspNetCore.SignalR.HubException("Method does not exist."),
                LaunchFailedEntered = launchFailed
            };
            var factory = DeferredFactory();

            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
                server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);
            orch.AcpFinalDrainBudget = TimeSpan.FromMilliseconds(200);

            await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-nf", repoPath));

            await launchFailed.Task.WaitAsync(WaitHarness.AcpHangGuard);

            await Assert.That(server.LaunchFailedCalls.Select(c => c.AgentId)).Contains("agent-nf");
            await Assert.That(server.AgentUnregisteredCalls).Contains("agent-nf");
            await Assert.That(factory.LastRuntime!.DisposeCount).IsGreaterThan(0);
            await Assert.That(factory.LastRuntime!.BeginFirstTurnCalls).IsEqualTo(0);
        } finally {
            cleanup();
        }
    }
}
