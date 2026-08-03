using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class McpWorkItemsServerTests {
    const string CapacitorSessionIdEnvVar = "KCAP_SESSION_ID";
    const string CodexThreadIdEnvVar      = "CODEX_THREAD_ID";

    // Shares ArgParsingTests' NotInParallel key: both suites mutate the same process-global
    // KCAP_SESSION_ID / CODEX_THREAD_ID env vars, so tests in either must not interleave.
    const string SessionEnvVarMutation = "SessionEnvVarMutation";

    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Resolve_session_id_prefers_explicit_argument() {
        var id = McpWorkItemsServer.ResolveSessionId(Args("""{"session_id":"explicit1"}"""));

        await Assert.That(id).IsEqualTo("explicit1");
    }

    [Test]
    public async Task Resolve_session_id_strips_dashes_from_explicit_argument() {
        // Matches ArgParsing.ResolveSessionIdFromEnv's normalization so an explicit dashed GUID
        // (e.g. copy-pasted from a UI) resolves to the same dashless key as the ambient env var.
        var id = McpWorkItemsServer.ResolveSessionId(Args("""{"session_id":"1234abcd-56ef-78ab-90cd-1234567890ab"}"""));

        await Assert.That(id).IsEqualTo("1234abcd56ef78ab90cd1234567890ab");
    }

    [Test]
    [NotInParallel(SessionEnvVarMutation)]
    public async Task Resolve_session_id_falls_back_to_env_when_argument_missing() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, "envsess1");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);

        try {
            var id = McpWorkItemsServer.ResolveSessionId(new JsonObject());

            await Assert.That(id).IsEqualTo("envsess1");
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, savedCdx);
        }
    }

    [Test]
    [NotInParallel(SessionEnvVarMutation)]
    public async Task Resolve_session_id_throws_when_neither_argument_nor_env_present() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);

        try {
            var ex = Assert.Throws<ArgumentException>(() => McpWorkItemsServer.ResolveSessionId(new JsonObject()));

            await Assert.That(ex!.Message).IsEqualTo(McpWorkItemsServer.NoSessionIdMessage);
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, savedCdx);
        }
    }

    [Test]
    public async Task Declare_body_carries_session_id_and_issue_key() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","issue_key":"PROJ-1234"}"""));

        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo("s1");
        await Assert.That(body["issue_key"]!.GetValue<string>()).IsEqualTo("PROJ-1234");
        await Assert.That(body["pr_number"]).IsNull();
        await Assert.That(body["work_item_id"]).IsNull();
        await Assert.That(body["new_title"]).IsNull();
    }

    [Test]
    public async Task Declare_body_carries_pr_number() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":123}"""));

        await Assert.That(body["pr_number"]!.GetValue<int>()).IsEqualTo(123);
    }

    [Test]
    public async Task Declare_body_carries_work_item_id() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","work_item_id":"wi-9"}"""));

        await Assert.That(body["work_item_id"]!.GetValue<string>()).IsEqualTo("wi-9");
    }

    [Test]
    public async Task Declare_body_carries_new_title() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","new_title":"Investigate flaky test"}"""));

        await Assert.That(body["new_title"]!.GetValue<string>()).IsEqualTo("Investigate flaky test");
    }

    [Test]
    public async Task Session_url_escapes_and_resolves_explicit_session_id() {
        var url = McpWorkItemsServer.BuildSessionUrl("http://x", Args("""{"session_id":"sess a/b"}"""));

        await Assert.That(url).IsEqualTo("http://x/api/work-items/session/sess%20a%2Fb");
    }

    // ── flow-review round 2 findings ─────────────────────────────────────────

    [Test]
    public async Task Decode_method_returns_null_for_wrong_shaped_method_instead_of_throwing() {
        // {"id":1,"method":{}} must yield an invalid-request response, never kill the stdio loop.
        var method = McpWorkItemsServer.DecodeMethod(Args("""{"id":1,"method":{}}"""));

        await Assert.That(method).IsNull();
    }

    [Test]
    public async Task Declare_body_rejects_string_pr_number_instead_of_dropping_it() {
        // A malformed two-selector declare (issue_key + string pr_number) must FAIL — silently
        // dropping the wrong-shaped selector would let the server's exactly-one rule pass and
        // perform an attach the caller never validly requested.
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","issue_key":"PROJ-1","pr_number":"123"}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Declare_body_rejects_object_shaped_pr_number() {
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":{}}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Declare_body_rejects_fractional_pr_number_via_raw_token() {
        // Raw-token validation (JsonElement.TryGetInt32) — a fractional part below double
        // precision must still reject; the lossy double round-trip would have accepted it.
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":2147483646.0000000001}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Declare_body_rejects_out_of_range_pr_number() {
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":2147483648}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Tools_list_exposes_the_declare_and_breakdown_surface() {
        var tools = McpWorkItemsServer.BuildToolsList();

        await Assert.That(tools.Select(t => t.Name).ToArray()).IsEquivalentTo(new[] {
            "declare_work_item", "get_session_work_items",
            "declare_work_breakdown", "retract_work_breakdown",
            "declare_work_relation", "retract_work_relation",
            "get_work_item_topology"
        });
    }

    // ── declared breakdown + relations ───────────────────────────────────────

    [Test]
    public async Task No_tool_accepts_a_server_owned_source_or_declared_by_argument() {
        // The server resolves Source/DeclaredBy from the authenticated caller and rejects a Source of
        // "user" outright. Accepting either here would be an argument the server ignores at best, and
        // a spoofing surface at worst — so the absence is asserted, not left to reviewer vigilance.
        foreach (var tool in McpWorkItemsServer.BuildToolsList()) {
            await Assert.That(tool.InputSchema.Properties.Keys).DoesNotContain("source")
                .Because($"{tool.Name} must not expose a server-owned field");
            await Assert.That(tool.InputSchema.Properties.Keys).DoesNotContain("declared_by")
                .Because($"{tool.Name} must not expose a server-owned field");
        }
    }

    [Test]
    public async Task Every_breakdown_tool_declares_its_ids_required() {
        // Unlike session_id, these ids have no ambient fallback — a schema that marked them optional
        // would invite a call with no id at all.
        var byName = McpWorkItemsServer.BuildToolsList().ToDictionary(t => t.Name);

        await Assert.That(byName["declare_work_breakdown"].InputSchema.Required).IsEquivalentTo(new[] { "parent_id", "part_ids" });
        await Assert.That(byName["retract_work_breakdown"].InputSchema.Required).IsEquivalentTo(new[] { "parent_id", "part_ids" });
        await Assert.That(byName["declare_work_relation"].InputSchema.Required).IsEquivalentTo(new[] { "from_id", "to_id", "relation_kind" });
        await Assert.That(byName["retract_work_relation"].InputSchema.Required).IsEquivalentTo(new[] { "from_id", "to_id", "relation_kind" });
        await Assert.That(byName["get_work_item_topology"].InputSchema.Required).IsEquivalentTo(new[] { "work_item_id" });
    }

    [Test]
    public async Task Array_properties_declare_their_element_type() {
        // An `array` with no `items` is incomplete JSON Schema: a strict client can reject it and a
        // model has to guess the element type.
        foreach (var tool in McpWorkItemsServer.BuildToolsList()) {
            foreach (var (name, property) in tool.InputSchema.Properties) {
                if (property.Type != "array") continue;

                await Assert.That(property.Items).IsNotNull()
                    .Because($"{tool.Name}.{name} is an array and must declare items");
                await Assert.That(property.Items!.Type).IsEqualTo("string");
            }
        }
    }

    [Test]
    public async Task Item_url_builds_the_route_and_escapes_the_id() {
        var url = McpWorkItemsServer.ItemUrl("http://x", Args("""{"parent_id":"wi 1/2"}"""), "parent_id", "breakdown");

        // The escape is what stops an id containing a slash from walking out of its path segment into
        // a different route.
        await Assert.That(url).IsEqualTo("http://x/api/work-items/wi%201%2F2/breakdown");
    }

    [Test]
    public async Task Item_url_rejects_a_missing_blank_or_wrong_typed_id() {
        var missing = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.ItemUrl("http://x", new JsonObject(), "parent_id", "breakdown"));
        await Assert.That(missing!.Message).Contains("parent_id");

        var blank = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.ItemUrl("http://x", Args("""{"parent_id":"   "}"""), "parent_id", "breakdown"));
        await Assert.That(blank!.Message).Contains("blank");

        var wrongType = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.ItemUrl("http://x", Args("""{"parent_id":42}"""), "parent_id", "breakdown"));
        await Assert.That(wrongType!.Message).Contains("string");
    }

    [Test]
    public async Task Breakdown_body_carries_part_ids() {
        var body = McpWorkItemsServer.BuildBreakdownBody(Args("""{"parent_id":"p1","part_ids":["a","b"]}"""));

        // parent_id rides the URL, not the body — sending it twice invites the two copies to diverge.
        await Assert.That(body["parent_id"]).IsNull();
        await Assert.That(body["part_ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray())
            .IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task Breakdown_body_rejects_a_wrong_shaped_part_ids_instead_of_dropping_it() {
        // Silently omitting a malformed part_ids would turn a bad declare into a differently-shaped
        // request whose rejection reads as though the caller had sent nothing.
        var notArray = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildBreakdownBody(Args("""{"parent_id":"p1","part_ids":"a"}""")));
        await Assert.That(notArray!.Message).Contains("array");

        var notStrings = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildBreakdownBody(Args("""{"parent_id":"p1","part_ids":[1,2]}""")));
        await Assert.That(notStrings!.Message).Contains("strings");

        var blankEntry = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildBreakdownBody(Args("""{"parent_id":"p1","part_ids":["a","  "]}""")));
        await Assert.That(blankEntry!.Message).Contains("blank");
    }

    [Test]
    public async Task Breakdown_body_leaves_an_absent_part_ids_to_the_server_to_reject() {
        // Deliberate pass-through: the server owns the "empty parts" rule and names it in a coded 400.
        var body = McpWorkItemsServer.BuildBreakdownBody(Args("""{"parent_id":"p1"}"""));

        await Assert.That(body["part_ids"]).IsNull();
    }

    [Test]
    public async Task Relation_body_carries_to_id_and_relation_kind() {
        var body = McpWorkItemsServer.BuildRelationBody(Args("""{"from_id":"a","to_id":"b","relation_kind":"blocks"}"""));

        await Assert.That(body["from_id"]).IsNull(); // rides the URL
        await Assert.That(body["to_id"]!.GetValue<string>()).IsEqualTo("b");
        await Assert.That(body["relation_kind"]!.GetValue<string>()).IsEqualTo("blocks");
    }

    [Test]
    public async Task Relation_body_does_not_enumerate_the_relation_kind_vocabulary() {
        // The server owns the vocabulary. Passing an unknown kind through means the caller gets the
        // server's coded rejection naming the real reason, rather than a client-side guess that could
        // drift from the server as kinds are added.
        var body = McpWorkItemsServer.BuildRelationBody(Args("""{"from_id":"a","to_id":"b","relation_kind":"depends_on"}"""));

        await Assert.That(body["relation_kind"]!.GetValue<string>()).IsEqualTo("depends_on");
    }
}
