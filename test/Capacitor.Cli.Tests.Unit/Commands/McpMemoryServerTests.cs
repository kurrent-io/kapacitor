using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpMemoryServerTests {
    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Search_url_includes_repo_machine_and_query() {
        var url = McpMemoryServer.BuildSearchUrl("http://x", Args("""{"query":"utc clock","limit":5}"""), "abc123", "mach-1");

        await Assert.That(url).IsEqualTo("http://x/api/memories/search?q=utc%20clock&repo=abc123&machine=mach-1&limit=5");
    }

    [Test]
    public async Task Search_url_omits_missing_context() {
        var url = McpMemoryServer.BuildSearchUrl("http://x", Args("""{"query":"a"}"""), null, null);

        await Assert.That(url).IsEqualTo("http://x/api/memories/search?q=a");
    }

    [Test]
    public async Task Save_body_defaults_to_cwd_repo_and_no_machine_tag() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"feedback"}"""),
            "abc123", "mach-1");

        await Assert.That(body["repo_hash"]!.GetValue<string>()).IsEqualTo("abc123");
        await Assert.That(body["machine_tag"]).IsNull();
        await Assert.That(body["harness"]!.GetValue<string>()).IsEqualTo("mcp");
    }

    [Test]
    public async Task Save_body_honors_global_and_machine_specific() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"user","slug":"s","description":"d","content":"c","kind":"preference","global":true,"machine_specific":true}"""),
            "abc123", "mach-1");

        await Assert.That(body["repo_hash"]).IsNull();
        await Assert.That(body["machine_tag"]!.GetValue<string>()).IsEqualTo("mach-1");
    }

    [Test]
    public async Task Save_body_throws_without_repo_context_unless_global() {
        var argsWithoutGlobal = Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"feedback"}""");

        await Assert.That(() => McpMemoryServer.BuildSaveBody(argsWithoutGlobal, null, "mach-1"))
            .Throws<ArgumentException>();

        var argsWithGlobal = Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"feedback","global":true}""");
        var body           = McpMemoryServer.BuildSaveBody(argsWithGlobal, null, "mach-1");

        await Assert.That(body["repo_hash"]).IsNull();
    }

    [Test]
    public async Task Save_body_throws_for_machine_specific_without_machine_id() {
        var args = Args("""{"audience":"user","slug":"s","description":"d","content":"c","kind":"preference","global":true,"machine_specific":true}""");

        await Assert.That(() => McpMemoryServer.BuildSaveBody(args, "abc123", null))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Save_body_always_carries_machine_context() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"feedback"}"""),
            "abc123", "mach-1");

        await Assert.That(body["machine_context"]!.GetValue<string>()).IsEqualTo("mach-1");
        await Assert.That(body["machine_tag"]).IsNull();
    }

    [Test]
    public async Task Save_body_carries_audience_project_for_project_audience() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"project","slug":"s","description":"d","content":"c","kind":"project","audience_project":"capacitor"}"""),
            "abc123", "mach-1");

        await Assert.That(body["audience"]!.GetValue<string>()).IsEqualTo("project");
        await Assert.That(body["audience_project"]!.GetValue<string>()).IsEqualTo("capacitor");
    }

    [Test]
    public async Task Save_body_omits_audience_project_when_absent() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"feedback"}"""),
            "abc123", "mach-1");

        await Assert.That(body["audience_project"]).IsNull();
    }

    [Test]
    public async Task Save_body_carries_project_and_drops_repo_hash() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"project","project":"capacitor"}"""),
            "abc123", "mach-1");

        await Assert.That(body["project"]!.GetValue<string>()).IsEqualTo("capacitor");
        await Assert.That(body["repo_hash"]).IsNull();
    }

    [Test]
    public async Task Save_body_with_project_needs_no_repo_context() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"project","project":"capacitor"}"""),
            null, "mach-1");

        await Assert.That(body["project"]!.GetValue<string>()).IsEqualTo("capacitor");
        await Assert.That(body["repo_hash"]).IsNull();
    }

    [Test]
    public async Task Save_body_rejects_blank_project() {
        // A present-but-blank slug is malformed, not omitted: falling back to the repo or org home would
        // silently widen where the memory surfaces.
        var args = Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"project","project":"   "}""");

        await Assert.That(() => McpMemoryServer.BuildSaveBody(args, "abc123", "mach-1"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Save_body_keeps_place_project_and_audience_project_apart() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"project","slug":"s","description":"d","content":"c","kind":"project","audience_project":"people","project":"place"}"""),
            "abc123", "mach-1");

        await Assert.That(body["audience_project"]!.GetValue<string>()).IsEqualTo("people");
        await Assert.That(body["project"]!.GetValue<string>()).IsEqualTo("place");
    }

    [Test]
    public async Task Get_url_escapes_slug_and_carries_context() {
        var url = McpMemoryServer.BuildGetUrl("http://x", Args("""{"id_or_slug":"my-slug"}"""), "abc123", "mach-1");

        await Assert.That(url).IsEqualTo("http://x/api/memories/my-slug?repo=abc123&machine=mach-1");
    }

    [Test]
    public async Task Rescope_body_carries_audience_and_team() {
        var body = McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","audience":"team","team":"payments"}"""));

        await Assert.That(body["audience"]!.GetValue<string>()).IsEqualTo("team");
        await Assert.That(body["team"]!.GetValue<string>()).IsEqualTo("payments");
        await Assert.That(body["project"]).IsNull();
    }

    [Test]
    public async Task Rescope_body_carries_project_without_audience() {
        var body = McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","project":"capacitor"}"""));

        await Assert.That(body["project"]!.GetValue<string>()).IsEqualTo("capacitor");
        await Assert.That(body["audience"]).IsNull();
    }

    [Test]
    public async Task Rescope_body_carries_audience_project_for_project_audience() {
        var body = McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","audience":"project","audience_project":"capacitor"}"""));

        await Assert.That(body["audience"]!.GetValue<string>()).IsEqualTo("project");
        await Assert.That(body["audience_project"]!.GetValue<string>()).IsEqualTo("capacitor");
        // Distinct from the place-axis project, which stays absent here.
        await Assert.That(body["project"]).IsNull();
    }

    [Test]
    public async Task Rescope_body_throws_without_audience_or_project() {
        await Assert.That(() => McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1"}""")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Rescope_body_treats_whitespace_project_as_absent() {
        // A blank slug never resolves server-side, so it counts as absent → the required-one-of check fires.
        await Assert.That(() => McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","project":"   "}""")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Tools_list_has_six_tools() {
        var tools = McpMemoryServer.BuildToolsList();

        await Assert.That(tools.Length).IsEqualTo(6);
        await Assert.That(tools.Select(t => t.Name).ToArray()).Contains("save_memory");
    }

    [Test]
    public async Task Save_tool_schema_exposes_place_project_beside_audience_project() {
        var save = McpMemoryServer.BuildToolsList().Single(t => t.Name == "save_memory");

        await Assert.That(save.InputSchema.Properties.Keys).Contains("project");
        await Assert.That(save.InputSchema.Properties.Keys).Contains("audience_project");
        await Assert.That(save.InputSchema.Required).DoesNotContain("project");
    }
}
