using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Unit;

public class CoordinationNoticesEmitterTests {
    static JsonObject Response(params string[] texts) {
        var arr = new JsonArray();
        foreach (var t in texts) arr.Add(new JsonObject { ["text"] = t });
        return new JsonObject { ["coordination_notices"] = arr };
    }

    [Test]
    public async Task Renders_heading_and_each_notice_as_a_bullet() {
        var body = Response(
            "Alex is also working on the auth refactor (AUTH-12).",
            "2 sessions touch billing/invoice.ts right now.");

        var fragment = CoordinationNoticesEmitter.BuildFragment(body, disabled: false);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("## Coordination notices");
        await Assert.That(fragment!).Contains("- Alex is also working on the auth refactor (AUTH-12).");
        await Assert.That(fragment!).Contains("- 2 sessions touch billing/invoice.ts right now.");
    }

    [Test]
    public async Task Renders_a_plus_n_more_tail_line_verbatim_as_a_bullet() {
        // The server may append a "+N more in the notification centre" tail as a normal {text} entry.
        var body = Response("Someone else is on the same bug.", "+3 more in the notification centre");

        var fragment = CoordinationNoticesEmitter.BuildFragment(body, disabled: false);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("- +3 more in the notification centre");
    }

    [Test]
    public async Task Returns_null_when_disabled() {
        var body = Response("x");
        await Assert.That(CoordinationNoticesEmitter.BuildFragment(body, disabled: true)).IsNull();
    }

    [Test]
    public async Task Returns_null_when_coordination_notices_empty() {
        var body = new JsonObject { ["coordination_notices"] = new JsonArray() };
        await Assert.That(CoordinationNoticesEmitter.BuildFragment(body, disabled: false)).IsNull();
    }

    [Test]
    public async Task Returns_null_when_coordination_notices_missing() {
        var body = new JsonObject { ["version"] = "1.0.0" };
        await Assert.That(CoordinationNoticesEmitter.BuildFragment(body, disabled: false)).IsNull();
    }

    [Test]
    public async Task Skips_entries_with_no_or_blank_text() {
        var body = new JsonObject {
            ["coordination_notices"] = new JsonArray(
                new JsonObject { ["text"] = "real notice" },
                new JsonObject { },                       // no text
                new JsonObject { ["text"] = "" },          // empty
                new JsonObject { ["text"] = "   " }        // whitespace
            )
        };

        var fragment = CoordinationNoticesEmitter.BuildFragment(body, disabled: false);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("- real notice");
        var bullets = fragment.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        await Assert.That(bullets).IsEqualTo(1);
    }

    [Test]
    public async Task Returns_null_when_every_notice_is_blank() {
        var body = new JsonObject {
            ["coordination_notices"] = new JsonArray(
                new JsonObject { ["text"] = "" },
                new JsonObject { ["text"] = "   " }
            )
        };
        await Assert.That(CoordinationNoticesEmitter.BuildFragment(body, disabled: false)).IsNull();
    }

    [Test]
    public async Task Fail_open_when_coordination_notices_is_a_string_not_array() {
        // A malformed field must never throw and must render nothing (fail-open).
        var body = JsonNode.Parse("""{ "coordination_notices": "v1-echoed-back" }""");
        await Assert.That(CoordinationNoticesEmitter.BuildFragment(body, disabled: false)).IsNull();
    }

    [Test]
    public async Task Fail_open_when_an_entry_text_is_a_number_not_a_string() {
        var body = JsonNode.Parse("""{ "coordination_notices": [ { "text": 42 }, { "text": "kept" } ] }""");
        var fragment = CoordinationNoticesEmitter.BuildFragment(body, disabled: false);
        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("- kept");
        var bullets = fragment.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        await Assert.That(bullets).IsEqualTo(1);
    }

    [Test]
    public async Task Returns_null_when_response_is_top_level_array() {
        var body = JsonNode.Parse("""[ { "text": "x" } ]""");
        await Assert.That(CoordinationNoticesEmitter.BuildFragment(body, disabled: false)).IsNull();
    }

    [Test]
    public async Task Returns_null_when_response_node_is_null() {
        await Assert.That(CoordinationNoticesEmitter.BuildFragment(null, disabled: false)).IsNull();
    }

    [Test]
    public async Task Fragment_is_not_a_json_envelope() {
        var fragment = CoordinationNoticesEmitter.BuildFragment(Response("mind the overlap"), disabled: false);
        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!.TrimStart().StartsWith('{')).IsFalse();
        await Assert.That(fragment).DoesNotContain("hookSpecificOutput");
        await Assert.That(fragment).DoesNotContain("hookEventName");
    }
}
