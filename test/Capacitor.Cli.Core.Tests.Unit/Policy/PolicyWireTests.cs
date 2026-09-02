namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class PolicyWireTests {
    [Test]
    public async Task Raw_payload_is_capped_with_a_visible_flag() {
        var big = new string('x', 20_000);
        var a = new CanonicalAction {
            Kind = ActionKind.Other, Vendor = "claude", RawToolName = "T",
            RawPayloadJson = $"{{\"v\":\"{big}\"}}",
        };
        var wire = PolicyWire.ToWire(a);
        await Assert.That(wire.RawPayloadTruncated).IsTrue();
        await Assert.That(wire.RawPayload!.Length).IsLessThanOrEqualTo(PolicyWire.MaxRawPayloadBytes);
    }

    [Test]
    public async Task Segments_round_trip_as_string_arrays() {
        var analysis = ShellCommandAnalyzer.Analyze("git status && git diff");
        var a = new CanonicalAction {
            Kind = ActionKind.Shell, Vendor = "claude", Command = "git status && git diff",
            Analyzed = true, Segments = analysis.Segments,
        };
        var wire = PolicyWire.ToWire(a);
        await Assert.That(wire.Segments!.Length).IsEqualTo(2);
        await Assert.That(wire.Segments[1]).IsEquivalentTo(new[] { "git", "diff" });
    }

    [Test]
    public async Task Decision_event_serializes_snake_case_on_the_shared_context() {
        var evt = new PolicyDecisionEventV1(
            "sid", null, "claude", PolicySeams.ClaudePreToolUse, "snap", PolicyEngine.Version,
            "full", "deny", "deny",
            PolicyWire.ToWire(new CanonicalAction { Kind = ActionKind.Other, Vendor = "claude", RawToolName = "T" }),
            [new PolicyMatchedRuleV1("User", 0, "deny", null)],
            Degraded: false, FailureClass: null, CorrelationId: null, CorrelationAmbiguous: false,
            DecidedAt: "2026-09-02T00:00:00Z");
        var json = System.Text.Json.JsonSerializer.Serialize(evt, CapacitorJsonContext.Default.PolicyDecisionEventV1);
        await Assert.That(json).Contains("\"session_id\":\"sid\"");
        await Assert.That(json).Contains("\"requested_outcome\":\"deny\"");
        await Assert.That(json).Contains("\"engine_version\"");
    }
}
