using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using static Capacitor.Cli.Daemon.Tests.Unit.Services.AgentOrchestratorHarness;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentOrchestratorPermissionAttributionTests {
    const string S1 = "6ba7b8109dad11d180b400c04fd430c8";

    static AgentInstance Agent(string id, string worktree, string? sessionId = null) =>
        new(id, null, "", null, "/repo", "claude", new FakeHostedAgentRuntime("claude", true),
            new WorktreeInfo(worktree, "b", "/repo"), new CancellationTokenSource()) { SessionId = sessionId };

    static AgentOrchestrator Build() =>
        BuildOrchestrator(new FakeServerConnectionForAttribution(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    [Test]
    public async Task Agent_id_rung_matches_raw_then_canonical_and_needs_exactly_one() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("6BA7B810-9DAD-11D1-80B4-00C04FD430C8", "/w1")); // a non-"N" key
        orch.RegisterAgentForTest(Agent("not-a-guid-key", "/w2"));

        await Assert.That(orch.HandleAttributePermission(new("6BA7B810-9DAD-11D1-80B4-00C04FD430C8", S1, null))!.Value.AgentId)
            .IsEqualTo("6BA7B810-9DAD-11D1-80B4-00C04FD430C8");                                   // raw
        await Assert.That(orch.HandleAttributePermission(new("6ba7b8109dad11d180b400c04fd430c8", S1, null))!.Value.AgentId)
            .IsEqualTo("6BA7B810-9DAD-11D1-80B4-00C04FD430C8");                                   // canonical
        await Assert.That(orch.HandleAttributePermission(new("not-a-guid-key", S1, null))!.Value.AgentId)
            .IsEqualTo("not-a-guid-key");                                                          // raw, non-GUID
        await Assert.That(orch.HandleAttributePermission(new("unknown", "ffffffffffffffffffffffffffffffff", null))).IsNull();
    }

    [Test]
    public async Task Session_rung_matches_any_guid_shape_and_falls_through_on_two_matches() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("a1", "/w1", sessionId: "6BA7B810-9DAD-11D1-80B4-00C04FD430C8"));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, null))!.Value.AgentId).IsEqualTo("a1");

        orch.RegisterAgentForTest(Agent("a2", "/w2", sessionId: S1));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, null))).IsNull();
    }

    [Test]
    public async Task Cwd_rung_matches_one_worktree_and_falls_through_on_a_shared_checkout() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("a1", "/repo/.capacitor/worktrees/agent-a1"));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, "/repo/.capacitor/worktrees/agent-a1/"))!.Value.AgentId).IsEqualTo("a1");

        orch.RegisterAgentForTest(Agent("b1", "/shared"));
        orch.RegisterAgentForTest(Agent("b2", "/shared"));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, "/shared"))).IsNull();
    }

    [Test]
    public async Task Malformed_session_id_is_unattributed() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("a1", "/w1", sessionId: S1));
        await Assert.That(orch.HandleAttributePermission(new("a1", "nope", "/w1"))).IsNull();
    }

    [Test]
    public async Task Teardown_withdraws_the_agents_pending_permissions_before_unpublishing() {
        await using var orch = Build();
        var agent = Agent("a1", "/w1");
        orch.RegisterAgentForTest(agent);
        var settlement = orch.PermissionBrokerForTest.Register(
            new("r1", "a1", S1, "claude", "Bash", null, null, false, false, "t"));

        orch.UnpublishAgentForTest("a1");
        var s = await settlement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(s.Source).IsEqualTo("agent_gone");
    }

    sealed class FakeServerConnectionForAttribution() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerConnection>.Instance);
}
