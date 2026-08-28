using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Covers <see cref="AgentOrchestrator.SnapshotAgentsForStatus"/> (the supervision payload's
/// agent rows) and the three mutate-then-pulse helpers (<c>SetAgentStatus</c>/<c>PublishAgent</c>/
/// <c>UnpublishAgent</c>) that are now the only writers of agent status and registry membership.
/// No socket/<see cref="LocalControlServer"/> involved — these tests drive the orchestrator +
/// <see cref="DaemonStatusNotifier"/> directly, mirroring <c>LocalControlHelloTests.StartAsync</c>'s
/// bare-orchestrator construction without the socket plumbing.
/// </summary>
public class AgentStatusSnapshotTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

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

    sealed record Fixture(AgentOrchestrator Orchestrator, DaemonStatusNotifier Notifier, TempDaemonStore Daemons) {
        public async Task CleanupAsync() {
            await Orchestrator.DisposeAsync();
            Daemons.Dispose();
        }
    }

    Fixture Build() {
        var daemons = new TempDaemonStore();

        var config = new DaemonConfig {
            Name         = "status-snapshot-test",
            ServerUrl    = "http://127.0.0.1:1",
            Store        = daemons.Store,
            WorktreeRoot = daemons.PathTo("worktrees"),
        };

        // The consent store and its decision log share this daemon's per-name state root, as DaemonRunner wires them.
        var store       = new LaunchConsentStore(config.Store.StateDirectory(config.Name), NullLogger.Instance);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(config.Store.StateDirectory(config.Name), NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        var connection       = new ServerConnection(config, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance);
        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var permissionBridge = new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance);
        var notifier         = new DaemonStatusNotifier();

        var orchestrator = new AgentOrchestrator(
            config, Config.Root, Home, connection, worktreeManager, repoMatcher,
            new NoopPtyProcessFactory(), new NoopHttpClientFactory(),
            permissionBridge, new Dictionary<string, IHostedAgentLauncher>(),
            new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance, gate, statusNotifier: notifier);

        return new Fixture(orchestrator, notifier, daemons);
    }

    [Test]
    public async Task Snapshot_orders_by_created_at_then_id_ordinal_and_includes_all_statuses() {
        var fixture = Build();
        var orch    = fixture.Orchestrator;
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
            await fixture.CleanupAsync();
        }
    }

    [Test]
    public async Task Snapshot_maps_kind_spellings_requester_and_nullables() {
        var fixture = Build();
        var orch    = fixture.Orchestrator;
        try {
            var createdAt = new DateTime(2026, 8, 1, 12, 30, 0, DateTimeKind.Utc);
            orch.SeedAgentForTest("r1", kind: LaunchKind.ReviewFlow, flowRunId: "flow_1",
                flowRole: "reviewer", requester: "github:12345", requesterDisplay: "Ada Lovelace", createdAt: createdAt);
            orch.SeedAgentForTest("d1"); // defaults: LaunchKind.Default, no flow identity, no requester

            var byId = orch.SnapshotAgentsForStatus().ToDictionary(a => a.Id);

            await Assert.That(byId["r1"].Kind).IsEqualTo("review-flow");
            await Assert.That(byId["r1"].Requester).IsEqualTo("github:12345");
            await Assert.That(byId["r1"].RequesterDisplay).IsEqualTo("Ada Lovelace");
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
            await Assert.That(byId["d1"].RequesterDisplay).IsNull();
            await Assert.That(byId["d1"].FlowRunId).IsNull();
        } finally {
            await fixture.CleanupAsync();
        }
    }

    /// <summary>A blank/whitespace <c>Model</c> is the orchestrator's
    /// "no model" sentinel (local spawns store "" verbatim; see
    /// <c>AgentOrchestrator.HandleLocalSpawnAsync</c>), but the wire contract represents an
    /// absent model as JSON <c>null</c>. The snapshot mapping must normalize at the wire
    /// boundary rather than leak the sentinel.</summary>
    [Test]
    public async Task Snapshot_normalizes_blank_model_to_null_and_passes_real_model_verbatim() {
        var fixture = Build();
        var orch    = fixture.Orchestrator;
        try {
            orch.SeedAgentForTest("blank-model", model: "");
            orch.SeedAgentForTest("real-model",  model: "gpt-5-codex");

            var byId = orch.SnapshotAgentsForStatus().ToDictionary(a => a.Id);

            await Assert.That(byId["blank-model"].Model).IsNull();
            await Assert.That(byId["real-model"].Model).IsEqualTo("gpt-5-codex");
        } finally {
            await fixture.CleanupAsync();
        }
    }

    /// <summary>
    /// Pins <see cref="AgentOrchestrator.SnapshotAgentsForStatus"/>'s stamping of
    /// <c>HasTerminal</c> from the agent's own runtime — a PTY runtime (<c>SeedAgentForTest</c>'s
    /// default, mirroring <see cref="PtyHostedAgentRuntime"/>) reports true, a non-PTY/ACP runtime
    /// (<see cref="FakeAcpRuntime"/>, seeded via <see cref="AgentOrchestratorHarness.SeedAcpAgent"/>
    /// since <c>SeedAgentForTest</c> only builds PTY runtimes) reports false. Asserted on the
    /// SERIALIZED wire payload, not the DTO field, so a naming-policy regression on the
    /// snake_case <c>has_terminal</c> member would fail this too.
    /// </summary>
    [Test]
    public async Task Status_payload_carries_has_terminal_per_runtime() {
        var fixture = Build();
        var orch    = fixture.Orchestrator;
        try {
            orch.SeedAgentForTest("pty-1");
            AgentOrchestratorHarness.SeedAcpAgent(orch, "acp-1", new FakeAcpRuntime());

            var snapshot = orch.SnapshotAgentsForStatus();
            var ptyJson  = JsonSerializer.Serialize(
                snapshot.Single(a => a.Id == "pty-1"), StatusIpcJsonContext.Default.AgentStatusDto);
            var acpJson = JsonSerializer.Serialize(
                snapshot.Single(a => a.Id == "acp-1"), StatusIpcJsonContext.Default.AgentStatusDto);

            await Assert.That(ptyJson).Contains("\"has_terminal\":true");
            await Assert.That(acpJson).Contains("\"has_terminal\":false");
        } finally {
            await fixture.CleanupAsync();
        }
    }

    [Test]
    public async Task Snapshot_title_is_first_line_truncated_or_null() {
        var fx = Build();
        try {
            fx.Orchestrator.SeedAgentForTest("t-short", prompt: "Fix the flaky test");
            fx.Orchestrator.SeedAgentForTest("t-multi", prompt: "\n  First real line  \nsecond line");
            fx.Orchestrator.SeedAgentForTest("t-long",  prompt: new string('x', 200));
            fx.Orchestrator.SeedAgentForTest("t-blank", prompt: "   \n  ");
            fx.Orchestrator.SeedAgentForTest("t-none");

            var byId = fx.Orchestrator.SnapshotAgentsForStatus().ToDictionary(a => a.Id);

            await Assert.That(byId["t-short"].Title).IsEqualTo("Fix the flaky test");
            await Assert.That(byId["t-multi"].Title).IsEqualTo("First real line");
            await Assert.That(byId["t-long"].Title!.Length).IsEqualTo(80);
            await Assert.That(byId["t-long"].Title).EndsWith("…");
            await Assert.That(byId["t-blank"].Title).IsNull();
            await Assert.That(byId["t-none"].Title).IsNull();

            // The wire boundary, not just the in-memory DTO (spec §1).
            var json = JsonSerializer.Serialize(byId["t-short"], StatusIpcJsonContext.Default.AgentStatusDto);
            await Assert.That(json).Contains("\"title\":\"Fix the flaky test\"");
            var jsonNone = JsonSerializer.Serialize(byId["t-none"], StatusIpcJsonContext.Default.AgentStatusDto);
            await Assert.That(jsonNone).Contains("\"title\":null");
        } finally { await fx.CleanupAsync(); }
    }

    [Test]
    public async Task Publish_status_change_and_unpublish_each_advance_the_generation() {
        var fixture = Build();
        var orch     = fixture.Orchestrator;
        var notifier = fixture.Notifier;
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
            await fixture.CleanupAsync();
        }
    }

    [Test]
    public async Task Status_payload_carries_transcript_path_null_before_discovery_and_the_value_after() {
        var fixture = Build();
        var orch    = fixture.Orchestrator;
        try {
            var agent = orch.SeedAgentForTest("pty-1");

            var before = System.Text.Json.JsonSerializer.Serialize(orch.SnapshotAgentsForStatus()[0], StatusIpcJsonContext.Default.AgentStatusDto);
            await Assert.That(before).Contains("\"transcript_path\":null");

            var versionBefore = fixture.Notifier.Version;
            await orch.RunDiscoveryForTest(agent, _ => ("0123456789abcdef0123456789abcdef", "/home/u/.claude/projects/-repo/t.jsonl"));

            var after = System.Text.Json.JsonSerializer.Serialize(orch.SnapshotAgentsForStatus()[0], StatusIpcJsonContext.Default.AgentStatusDto);
            await Assert.That(after).Contains("\"transcript_path\":\"/home/u/.claude/projects/-repo/t.jsonl\"");
            await Assert.That(agent.SessionId).IsEqualTo("0123456789abcdef0123456789abcdef");
            await Assert.That(fixture.Notifier.Version).IsGreaterThan(versionBefore);
        } finally {
            await fixture.CleanupAsync();
        }
    }

    /// A session id learned elsewhere must not stop discovery: the path is the obligation.
    [Test]
    public async Task Discovery_sets_the_path_even_when_the_session_id_is_already_known() {
        var fixture = Build();
        var orch    = fixture.Orchestrator;
        try {
            var agent = orch.SeedAgentForTest("pty-2");
            agent.SessionId = "pre-known";

            await orch.RunDiscoveryForTest(agent, _ => ("other", "/t.jsonl"));

            await Assert.That(agent.SessionId).IsEqualTo("pre-known");
            await Assert.That(agent.TranscriptPath).IsEqualTo("/t.jsonl");
        } finally {
            await fixture.CleanupAsync();
        }
    }

    /// A private agent gets the path and the pulse too.
    [Test]
    public async Task A_private_agent_gets_its_path_and_pulse() {
        var fixture = Build();
        var orch    = fixture.Orchestrator;
        try {
            var agent = orch.SeedAgentForTest("priv-1", isPrivate: true);
            var versionBefore = fixture.Notifier.Version;

            await orch.RunDiscoveryForTest(agent, _ => ("sid", "/p.jsonl"));

            await Assert.That(agent.TranscriptPath).IsEqualTo("/p.jsonl");
            await Assert.That(fixture.Notifier.Version).IsGreaterThan(versionBefore);
        } finally {
            await fixture.CleanupAsync();
        }
    }
}
