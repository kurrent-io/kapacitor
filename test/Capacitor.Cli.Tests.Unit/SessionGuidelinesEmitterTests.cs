using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Unit;

public class SessionGuidelinesEmitterTests {
    static JsonArray Top(params (string text, string category)[] items) {
        var arr = new JsonArray();
        foreach (var (text, cat) in items) {
            arr.Add(new JsonObject { ["text"] = text, ["category"] = cat });
        }
        return arr;
    }

    [Test]
    public async Task Renders_both_blocks_when_mixed() {
        var body = new JsonObject {
            ["top_clusters"] = Top(
                ("tests need docker", "quality"),
                ("run tests first",   "agent_guidance"),
                ("seal secrets",      "safety"),
                ("avoid force-push",  "agent_guidance")
            )
        };

        var fragment = SessionGuidelinesEmitter.BuildFragment(body, disabled: false);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("## Known patterns");
        await Assert.That(fragment!).Contains("- tests need docker");
        await Assert.That(fragment!).Contains("- seal secrets");
        await Assert.That(fragment!).Contains("## Guidance from past sessions");
        await Assert.That(fragment!).Contains("- run tests first");
        await Assert.That(fragment!).Contains("- avoid force-push");
    }

    [Test]
    public async Task Renders_only_guidance_block_when_only_agent_guidance() {
        var body = new JsonObject {
            ["top_clusters"] = Top(("run tests first", "agent_guidance"))
        };

        var fragment = SessionGuidelinesEmitter.BuildFragment(body, disabled: false);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("## Guidance from past sessions");
        await Assert.That(fragment!).Contains("- run tests first");
        await Assert.That(fragment!).DoesNotContain("## Known patterns");
    }

    [Test]
    public async Task Renders_only_patterns_block_when_no_agent_guidance() {
        var body = new JsonObject {
            ["top_clusters"] = Top(("tests need docker", "quality"))
        };

        var fragment = SessionGuidelinesEmitter.BuildFragment(body, disabled: false);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("## Known patterns");
        await Assert.That(fragment!).Contains("- tests need docker");
        await Assert.That(fragment!).DoesNotContain("## Guidance from past sessions");
    }

    [Test]
    public async Task Returns_null_when_disabled() {
        var body = new JsonObject {
            ["top_clusters"] = Top(("x", "agent_guidance"))
        };
        await Assert.That(SessionGuidelinesEmitter.BuildFragment(body, disabled: true)).IsNull();
    }

    [Test]
    public async Task Returns_null_when_top_clusters_empty() {
        var body = new JsonObject { ["top_clusters"] = new JsonArray() };
        await Assert.That(SessionGuidelinesEmitter.BuildFragment(body, disabled: false)).IsNull();
    }

    [Test]
    public async Task Returns_null_when_top_clusters_missing() {
        var body = new JsonObject();
        await Assert.That(SessionGuidelinesEmitter.BuildFragment(body, disabled: false)).IsNull();
    }

    [Test]
    public async Task Skips_entries_with_no_text() {
        var body = new JsonObject {
            ["top_clusters"] = new JsonArray(
                new JsonObject { ["text"] = "good", ["category"] = "agent_guidance" },
                new JsonObject {                     ["category"] = "agent_guidance" },  // no text
                new JsonObject { ["text"] = "",      ["category"] = "agent_guidance" }   // empty text
            )
        };

        var fragment = SessionGuidelinesEmitter.BuildFragment(body, disabled: false);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("- good");
        await Assert.That(fragment!.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task Returns_null_when_top_clusters_is_object_not_array() {
        var body = """{ "top_clusters": { "category": "safety", "text": "x" } }""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task Returns_null_when_response_is_top_level_array() {
        var body = """[ { "category": "safety", "text": "x" } ]""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task BuildFragment_skips_entries_with_blank_text() {
        var body = new JsonObject {
            ["top_clusters"] = new JsonArray(
                new JsonObject { ["text"] = "",    ["category"] = "safety" },
                new JsonObject { ["text"] = "   ", ["category"] = "safety" },
                new JsonObject { ["text"] = "real lesson", ["category"] = "quality" }
            )
        };
        var fragment = SessionGuidelinesEmitter.BuildFragment(body, disabled: false);
        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("- real lesson");
        var bullets = fragment.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        await Assert.That(bullets).IsEqualTo(1);
    }

    [Test]
    public async Task Fragment_is_not_a_json_envelope() {
        var body = new JsonObject {
            ["top_clusters"] = Top(("always close the writer", "safety"))
        };
        var fragment = SessionGuidelinesEmitter.BuildFragment(body, disabled: false);
        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!.TrimStart().StartsWith('{')).IsFalse();
        await Assert.That(fragment).DoesNotContain("hookSpecificOutput");
        await Assert.That(fragment).DoesNotContain("hookEventName");
    }
}
