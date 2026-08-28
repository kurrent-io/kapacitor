using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Requester stamping (spec §4.3): AgentInstance.RequesterUserId is captured from
/// LaunchAgentCommand at construction — non-null when a new server sends it, null for
/// old servers (field absent) — so the supervision payload can show who asked.
/// RequesterDisplay (issue #481) is captured the same way, independently — a server may
/// send the id without a display name (old server, or the server hasn't resolved one yet).
/// </summary>
public class AgentOrchestratorRequesterTests {
    [Test]
    public async Task Launch_stamps_RequesterUserId_from_the_command() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");

        var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

        var cmd = new LaunchAgentCommand(
            AgentId: "req-1",
            Prompt: "p",
            Model: "default",
            Effort: null,
            RepoPath: repoPath,
            Tools: null,
            AttachmentIds: null,
            Vendor: "claude",
            RequesterUserId: "github:12345",
            RequesterDisplay: "Ada Lovelace"
        );

        await orch.HandleLaunchAgentForTest(cmd);

        var agent = orch.GetAgentForTest("req-1")!;
        await Assert.That(agent.RequesterUserId).IsEqualTo("github:12345");
        await Assert.That(agent.RequesterDisplay).IsEqualTo("Ada Lovelace");

    }

    [Test]
    public async Task Launch_without_a_requester_leaves_RequesterUserId_null() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");

        var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

        var cmd = new LaunchAgentCommand(
            AgentId: "req-2",
            Prompt: "p",
            Model: "default",
            Effort: null,
            RepoPath: repoPath,
            Tools: null,
            AttachmentIds: null,
            Vendor: "claude"
        );

        await orch.HandleLaunchAgentForTest(cmd);

        var agent = orch.GetAgentForTest("req-2")!;
        await Assert.That(agent.RequesterUserId).IsNull();
        await Assert.That(agent.RequesterDisplay).IsNull();

    }

    [Test]
    public async Task SeedAgentForTest_with_no_requester_stamps_null() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = orch.SeedAgentForTest("seed-1");

        await Assert.That(agent.RequesterUserId).IsNull();
    }

    [Test]
    public async Task SeedAgentForTest_stamps_the_given_requester() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = orch.SeedAgentForTest("seed-2", requester: "github:99");

        await Assert.That(agent.RequesterUserId).IsEqualTo("github:99");
    }
}
