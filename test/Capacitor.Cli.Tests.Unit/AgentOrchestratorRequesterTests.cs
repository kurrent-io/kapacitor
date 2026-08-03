using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Requester stamping (spec §4.3): AgentInstance.RequesterUserId is captured from
/// LaunchAgentCommand at construction — non-null when a new server sends it, null for
/// old servers (field absent) — so the supervision payload can show who asked.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task Launch_stamps_RequesterUserId_from_the_command() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");

            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId: "req-1",
                Prompt: "p",
                Model: "default",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude",
                RequesterUserId: "github:12345"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(orch.GetAgentForTest("req-1")!.RequesterUserId).IsEqualTo("github:12345");
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Launch_without_a_requester_leaves_RequesterUserId_null() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");

            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

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

            await Assert.That(orch.GetAgentForTest("req-2")!.RequesterUserId).IsNull();
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task SeedAgentForTest_with_no_requester_stamps_null() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = orch.SeedAgentForTest("seed-1");

        await Assert.That(agent.RequesterUserId).IsNull();
    }

    [Test]
    public async Task SeedAgentForTest_stamps_the_given_requester() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = orch.SeedAgentForTest("seed-2", requester: "github:99");

        await Assert.That(agent.RequesterUserId).IsEqualTo("github:99");
    }
}
