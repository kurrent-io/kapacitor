using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Phase A (task A3): covers <see cref="AgentOrchestrator.HandleProbeBorrowSourceForTest"/>,
/// the handler behind the server→daemon <c>ProbeBorrowSource</c> hub method. The handler is a thin
/// mapping from <see cref="BorrowAuthorizer.AuthorizeBorrowAsync"/>'s <see cref="BorrowAuthResult"/>
/// onto the wire-facing <see cref="BorrowProbeResult"/> — the policy itself (allowlist, git-root,
/// symlink canonicalization) is already covered by <c>BorrowAuthorizerTests</c>.
/// </summary>
public class AgentOrchestratorProbeBorrowSourceTests {
    [Test]
    public async Task ProbeBorrowSource_allows_a_git_rooted_temp_dir() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var result = await orch.HandleProbeBorrowSourceForTest(repoPath);

        await Assert.That(result.CanBorrow).IsTrue();
        await Assert.That(result.CanonicalCwd).IsNotNull();
        await Assert.That(result.CanonicalGitRoot).IsNotNull();

    }

    [Test]
    public async Task ProbeBorrowSource_rejects_a_missing_path_with_path_absent_reason() {
        using var missingDir = TempDir.WithPathTo("kcap-probe-missing", out var missing);

        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var result = await orch.HandleProbeBorrowSourceForTest(missing);

        await Assert.That(result.CanBorrow).IsFalse();
        await Assert.That(result.Reason).IsEqualTo("path_absent");
        await Assert.That(result.CanonicalCwd).IsNull();
        await Assert.That(result.CanonicalGitRoot).IsNull();
    }
}
