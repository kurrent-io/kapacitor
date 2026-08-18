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
    public async Task Get_url_escapes_slug_and_carries_context() {
        var url = McpMemoryServer.BuildGetUrl("http://x", Args("""{"id_or_slug":"my-slug"}"""), "abc123", "mach-1");

        await Assert.That(url).IsEqualTo("http://x/api/memories/my-slug?repo=abc123&machine=mach-1");
    }

    [Test]
    public async Task Tools_list_has_six_tools() {
        var tools = McpMemoryServer.BuildToolsList();

        await Assert.That(tools.Length).IsEqualTo(6);
        await Assert.That(tools.Select(t => t.Name).ToArray()).Contains("save_memory");
    }
}
