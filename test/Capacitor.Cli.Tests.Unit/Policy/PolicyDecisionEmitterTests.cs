namespace Capacitor.Cli.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Policy;
using WireMock.Server;

public class PolicyDecisionEmitterTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    PolicyDecisionEmitter Emitter => new(Config.Root);

    /// <summary>Points the config's active profile at the stub server, so "no request reached it" is
    /// a live guard: any inline POST reintroduced here would resolve this URL and be logged.</summary>
    [Before(Test)]
    public void PointTheProfileAtTheStub() =>
        File.WriteAllText(Config.Root.Path("config.json"), $$"""
            {
              "version": 2,
              "active_profile": "default",
              "profiles": { "default": { "server_url": "{{_server.Url}}" } },
              "profile_bindings": {}
            }
            """);

    static PolicySnapshot Snapshot => new("snap1", [
        new PolicyScopeDocument(PolicyScope.User, "/u/approvals.yaml", "version: 1\n",
            PolicyDocumentBinder.Bind("version: 1\n", PolicyScope.User))], false, []);

    static PolicyDecisionEventV1 Event(string sid) => new(
        sid, null, "claude", PolicySeams.ClaudePreToolUse, "snap1", PolicyEngine.Version,
        "full", "deny", "deny",
        PolicyWire.ToWire(new CanonicalAction { Kind = ActionKind.Other, Vendor = "claude", RawToolName = "T" }),
        [], false, null, null, false, "2026-09-02T00:00:00Z");

    [Test]
    public async Task Spools_the_snapshot_once_then_a_line_per_decision() {
        await Emitter.EmitAsync(Event("s1"), Snapshot);
        await Emitter.EmitAsync(Event("s1"), Snapshot);

        var decisions = SpooledPolicyEvents.Decisions(Config.Root, "s1");
        await Assert.That(decisions.Count).IsEqualTo(2);
        await Assert.That(decisions[0]["session_id"]!.GetValue<string>()).IsEqualTo("s1");
        await Assert.That(decisions[0]["seam"]!.GetValue<string>()).IsEqualTo("claude_pre_tool_use");
        // The upload marker is what makes the second emit skip the snapshot.
        await Assert.That(SpooledPolicyEvents.Snapshots(Config.Root, "s1").Count).IsEqualTo(1);
    }

    /// <summary>Nothing is posted inline. A decision seam runs under the vendor's hook ceiling and
    /// its stdout is read only after the process exits, so a round trip here could outlive the hook
    /// and lose a deny that was already written.</summary>
    [Test]
    public async Task Emitting_makes_no_http_request() {
        await Emitter.EmitAsync(Event("s3"), Snapshot);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        // Non-vacuous: the event really was produced, just locally.
        await Assert.That(SpooledPolicyEvents.Decisions(Config.Root, "s3").Count).IsEqualTo(1);
    }

    /// <summary>The snapshot leads its decision in the file, because a decision names a snapshot id
    /// the server cannot resolve without it and the drain replays in arrival order.</summary>
    [Test]
    public async Task Snapshot_is_spooled_ahead_of_the_decision_it_names() {
        await Emitter.EmitAsync(Event("s4"), Snapshot);
        var routes = File.ReadAllLines(Path.Combine(Config.Root.Path("spool"), "s4.jsonl"))
            .Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!["route"]!.GetValue<string>())
            .ToList();
        await Assert.That(routes.Count).IsEqualTo(2);
        await Assert.That(routes[0]).IsEqualTo("policy-snapshot");
        await Assert.That(routes[1]).IsEqualTo("policy-decision");
    }
}
