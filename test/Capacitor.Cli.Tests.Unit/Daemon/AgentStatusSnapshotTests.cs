using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Covers <see cref="AgentOrchestrator.SnapshotAgentsForStatus"/> (the supervision payload's
/// agent rows) and the three mutate-then-pulse helpers (<c>SetAgentStatus</c>/<c>PublishAgent</c>/
/// <c>UnpublishAgent</c>) that are now the only writers of agent status and registry membership.
/// No socket/<see cref="LocalControlServer"/> involved — these tests drive the orchestrator +
/// <see cref="DaemonStatusNotifier"/> directly, mirroring <c>LocalControlHelloTests.StartAsync</c>'s
/// bare-orchestrator construction without the socket plumbing.
/// </summary>
public class AgentStatusSnapshotTests {
    sealed class NoopHostLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    sealed class NoopPtyProcessFactory : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string command, string[] args, string cwd,
                Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40
            ) => throw new NotSupportedException("AgentStatusSnapshotTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    static (AgentOrchestrator Orchestrator, DaemonStatusNotifier Notifier) Build() {
        var stateDir = Directory.CreateTempSubdirectory("kcap-status-snapshot-state-").FullName;
        var store       = new LaunchConsentStore(stateDir, NullLogger.Instance);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateDir, NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        var config = new DaemonConfig {
            Name         = "status-snapshot-test",
            ServerUrl    = "http://127.0.0.1:1",
            StateDir     = stateDir,
            WorktreeRoot = Path.Combine(Path.GetTempPath(), "kcap-status-snapshot-wt-" + Guid.NewGuid().ToString("N")[..8]),
        };

        var connection       = new ServerConnection(config, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance);
        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var permissionBridge = new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance);
        var notifier         = new DaemonStatusNotifier();

        var orchestrator = new AgentOrchestrator(
            config, connection, worktreeManager, repoMatcher, new NoopPtyProcessFactory(), new NoopHttpClientFactory(),
            permissionBridge, new Dictionary<string, IHostedAgentLauncher>(),
            new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance, gate, statusNotifier: notifier);

        return (orchestrator, notifier);
    }

    [Test]
    public async Task Snapshot_orders_by_created_at_then_id_ordinal_and_includes_all_statuses() {
        var (orch, _) = Build();
        try {
            var t0 = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
            orch.SeedAgentForTest("b-second", status: "Quarantined", createdAt: t0.AddMinutes(1));
            orch.SeedAgentForTest("z-first",  status: "Starting",    createdAt: t0);
            orch.SeedAgentForTest("a-tie",    status: "Completed",   createdAt: t0.AddMinutes(1));

            var agents = orch.SnapshotAgentsForStatus();

            await Assert.That(agents.Select(a => a.Id)).IsEquivalentTo(
                new[] { "z-first", "a-tie", "b-second" }, CollectionOrdering.Matching);
            // All statuses ride along verbatim — the vocabulary is open, PascalCase as stored.
            await Assert.That(agents.Select(a => a.Status)).IsEquivalentTo(
                new[] { "Starting", "Completed", "Quarantined" }, CollectionOrdering.Matching);
        } finally {
            await orch.DisposeAsync();
        }
    }

    [Test]
    public async Task Snapshot_maps_kind_spellings_requester_and_nullables() {
        var (orch, _) = Build();
        try {
            var createdAt = new DateTime(2026, 8, 1, 12, 30, 0, DateTimeKind.Utc);
            orch.SeedAgentForTest("r1", kind: LaunchKind.ReviewFlow, flowRunId: "flow_1",
                flowRole: "reviewer", requester: "github:12345", createdAt: createdAt);
            orch.SeedAgentForTest("d1"); // defaults: LaunchKind.Default, no flow identity, no requester

            var byId = orch.SnapshotAgentsForStatus().ToDictionary(a => a.Id);

            await Assert.That(byId["r1"].Kind).IsEqualTo("review-flow");
            await Assert.That(byId["r1"].Requester).IsEqualTo("github:12345");
            await Assert.That(byId["r1"].FlowRunId).IsEqualTo("flow_1");
            // SeedAgentForTest's fixed constants — pins the Select against a same-typed-neighbor
            // transposition (e.g. Vendor/RepoPath swapped) that the other assertions can't catch.
            await Assert.That(byId["r1"].Vendor).IsEqualTo("codex");
            await Assert.That(byId["r1"].RepoPath).IsEqualTo("/repo");
            await Assert.That(byId["r1"].Model).IsEqualTo("default");
            await Assert.That(byId["r1"].FlowRole).IsEqualTo("reviewer");
            await Assert.That(byId["r1"].CreatedAt).IsEqualTo(createdAt);
            await Assert.That(byId["d1"].Kind).IsEqualTo("agent");
            await Assert.That(byId["d1"].Requester).IsNull();
            await Assert.That(byId["d1"].FlowRunId).IsNull();
        } finally {
            await orch.DisposeAsync();
        }
    }

    [Test]
    public async Task Publish_status_change_and_unpublish_each_advance_the_generation() {
        var (orch, notifier) = Build();
        try {
            var v0 = notifier.Version;
            var agent = orch.SeedAgentForTest("gen-1"); // registers via PublishAgent
            await Assert.That(notifier.Version).IsGreaterThan(v0);

            var v1 = notifier.Version;
            orch.SetAgentStatus(agent, "Completed");
            await Assert.That(notifier.Version).IsGreaterThan(v1);

            var v2 = notifier.Version;
            orch.UnpublishAgent("gen-1");
            await Assert.That(notifier.Version).IsGreaterThan(v2);
            await Assert.That(orch.SnapshotAgentsForStatus()).IsEmpty();
        } finally {
            await orch.DisposeAsync();
        }
    }
}
