namespace Capacitor.Cli.Tests.Unit.Policy;

using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Policy;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

public class PolicyDecisionEmitterTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    PolicyDecisionEmitter Emitter => new(Config.Root, Resolutions.Of(new Profile(), serverUrl: _server.Url!));

    static PolicySnapshot Snapshot => new("snap1", [
        new PolicyScopeDocument(PolicyScope.User, "/u/approvals.yaml", "version: 1\n",
            PolicyDocumentBinder.Bind("version: 1\n", PolicyScope.User))], false, []);

    static PolicyDecisionEventV1 Event(string sid) => new(
        sid, null, "claude", PolicySeams.ClaudePreToolUse, "snap1", PolicyEngine.Version,
        "full", "deny", "deny",
        PolicyWire.ToWire(new CanonicalAction { Kind = ActionKind.Other, Vendor = "claude", RawToolName = "T" }),
        [], false, null, null, false, "2026-09-02T00:00:00Z");

    [Test]
    public async Task Uploads_snapshot_once_then_posts_each_decision() {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        await Emitter.EmitAsync(Event("s1"), Snapshot);
        await Emitter.EmitAsync(Event("s1"), Snapshot);
        var snapshots = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-snapshot").UsingPost());
        var decisions = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-decision").UsingPost());
        await Assert.That(snapshots.Count).IsEqualTo(1);
        await Assert.That(decisions.Count).IsEqualTo(2);
        var body = JsonNode.Parse(decisions[0].RequestMessage.Body!)!;
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo("s1");
        await Assert.That(body["seam"]!.GetValue<string>()).IsEqualTo("claude_pre_tool_use");
    }

    [Test]
    public async Task Server_outage_spools_instead_of_losing_the_decision() {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(503));
        await Emitter.EmitAsync(Event("s2"), Snapshot);
        await Assert.That(new HookSpool(Config.Root).HasBacklog("s2")).IsTrue();
    }
}
