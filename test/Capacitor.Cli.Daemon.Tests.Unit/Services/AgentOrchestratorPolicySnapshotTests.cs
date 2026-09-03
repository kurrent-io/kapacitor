using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Where the launch-bound policy documents reach the server. The runtime queues its opening prompt
/// inside StartAsync, so a permission decision can be enqueued the moment the runtime exists — and
/// the event lane preserves insertion order, which makes "before StartAsync" the only placement
/// that cannot leave the server reading a decision against a snapshot it has never seen.
/// </summary>
public class AgentOrchestratorPolicySnapshotTests {
    const string UserPolicy =
        "version: 1\nrules:\n  - match: { kind: shell, command: \"rm -rf*\" }\n    outcome: deny\n";

    [Test]
    public async Task Launch_uploads_the_policy_snapshot_before_the_runtime_starts() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();
        List<object>? enqueuedWhenTheRuntimeStarted = null;
        var cursorSpy = new SpyHostedAgentRuntimeFactory("cursor") {
            OnStart = () => {
                lock (server.RunEvents) enqueuedWhenTheRuntimeStarted = [.. server.RunEvents.Select(e => e.Event)];
            }
        };

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath,
            extraRuntimeFactories: [cursorSpy],
            configure: c => File.WriteAllText(c.ConfigRoot.Path("approvals.yaml"), UserPolicy));

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "policy-1", Prompt: "do work", Model: "auto", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "cursor"));

        await Assert.That(cursorSpy.StartCalls).IsEqualTo(1);
        var upload = enqueuedWhenTheRuntimeStarted!.OfType<PolicySnapshotUploadV1>().Single();
        // No vendor session id exists this early, so the run is keyed by the agent id.
        await Assert.That(upload.SessionId).IsEqualTo("policy-1");
        await Assert.That(upload.Documents.Any(d => d.Scope == "user")).IsTrue();
        // Enqueued once, not again by the registration that follows.
        await Assert.That(server.RunEvents.Select(e => e.Event).OfType<PolicySnapshotUploadV1>().Count()).IsEqualTo(1);
    }

    /// A launch with nothing to say leaves the lane to the run's own events.
    [Test]
    public async Task Launch_without_any_policy_documents_uploads_nothing() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server    = new CaptureServerConnection();
        var cursorSpy = new SpyHostedAgentRuntimeFactory("cursor");

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath,
            extraRuntimeFactories: [cursorSpy]);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "policy-2", Prompt: "do work", Model: "auto", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "cursor"));

        await Assert.That(server.RunEvents.Select(e => e.Event).OfType<PolicySnapshotUploadV1>()).IsEmpty();
    }
}
