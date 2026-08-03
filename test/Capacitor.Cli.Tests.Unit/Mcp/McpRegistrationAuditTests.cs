using System.Linq;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit.Mcp;

/// <summary>
/// Pins the structural (never name-only) duplicate classification over Claude's user config:
/// only a semantically canonical user-scope copy of a plugin-shipped kcap server is a removable
/// duplicate; a divergent same-name entry is a conflict and must be preserved — same name does
/// not imply ownership.
/// </summary>
public class McpRegistrationAuditTests {
    [Test]
    public async Task Detects_user_scope_canonical_duplicate_of_a_plugin_server() {
        var json = """
        { "mcpServers": { "kcap-flows": { "type": "stdio", "command": "kcap", "args": ["mcp","flows"] } } }
        """;

        var findings = McpRegistrationAudit.FindClaudeDuplicates(json);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Name).IsEqualTo("kcap-flows");
        await Assert.That(findings[0].Scope).IsEqualTo(McpRegistrationAudit.UserScope);
        await Assert.That(findings[0].Issue).IsEqualTo(McpRegistrationIssue.CanonicalDuplicate);
    }

    [Test]
    public async Task Detects_project_scope_canonical_duplicate() {
        var json = """
        { "projects": { "/w/repo": { "mcpServers": {
            "kcap-sessions": { "command": "kcap", "args": ["mcp","sessions"], "cwd": "/w/repo" } } } } }
        """;

        var findings = McpRegistrationAudit.FindClaudeDuplicates(json);

        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Scope).IsEqualTo("projects[/w/repo]");
        await Assert.That(findings[0].Issue).IsEqualTo(McpRegistrationIssue.CanonicalDuplicate);
    }

    [Test]
    public async Task Cosmetic_fields_do_not_break_canonical_classification() {
        // description + empty env are what the shipped plugin/older registrations carry.
        var json = """
        { "mcpServers": { "kcap-review": {
            "command": "kcap", "args": ["mcp","review"], "description": "anything", "env": {} } } }
        """;

        var findings = McpRegistrationAudit.FindClaudeDuplicates(json);
        await Assert.That(findings[0].Issue).IsEqualTo(McpRegistrationIssue.CanonicalDuplicate);
    }

    [Test]
    [Arguments("""{ "command": "kcap", "args": ["mcp","flows"], "env": { "KCAP_URL": "https://x" } }""")] // custom env
    [Arguments("""{ "command": "/some/other/tool", "args": ["mcp","flows"] }""")]                          // foreign command
    [Arguments("""{ "command": "kcap", "args": ["mcp","memory"] }""")]                                     // wrong args for name
    [Arguments("""{ "command": "kcap", "args": ["mcp","flows"], "custom": true }""")]                      // extra field
    [Arguments("""{ "command": "kcap", "args": ["mcp","flows","--extra"] }""")]                            // extra arg
    public async Task Divergent_same_name_entry_is_a_conflict_and_never_removed(string entryJson) {
        var json = $$"""{ "mcpServers": { "kcap-flows": {{entryJson}} } }""";

        var findings = McpRegistrationAudit.FindClaudeDuplicates(json);
        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Issue).IsEqualTo(McpRegistrationIssue.Conflict);

        // Conflict preservation: removal must leave the divergent entry byte-identical.
        var removed = McpRegistrationAudit.RemoveClaudeDuplicates(json);
        var servers = (JsonObject)JsonNode.Parse(removed)!["mcpServers"]!;
        await Assert.That(servers.ContainsKey("kcap-flows")).IsTrue();
        await Assert.That(JsonNode.DeepEquals(servers["kcap-flows"], JsonNode.Parse(entryJson))).IsTrue();
    }

    [Test]
    public async Task Ignores_non_kcap_servers_and_absent_block() {
        await Assert.That(McpRegistrationAudit.FindClaudeDuplicates("""{ "mcpServers": { "other": {} } }""")).IsEmpty();
        await Assert.That(McpRegistrationAudit.FindClaudeDuplicates("{}")).IsEmpty();
        await Assert.That(McpRegistrationAudit.FindClaudeDuplicates("not json at all")).IsEmpty();
        await Assert.That(McpRegistrationAudit.FindClaudeDuplicates("[1,2]")).IsEmpty();
    }

    [Test]
    public async Task Resolved_native_path_command_is_canonical_only_when_it_matches_exactly() {
        var json = """
        { "mcpServers": {
            "kcap-review":   { "command": "/opt/kcap/bin/kcap", "args": ["mcp","review"] },
            "kcap-sessions": { "command": "/somewhere/else/kcap", "args": ["mcp","sessions"] } } }
        """;

        var findings = McpRegistrationAudit.FindClaudeDuplicates(json, nativeBinaryPath: "/opt/kcap/bin/kcap");

        var review   = findings.Single(f => f.Name == "kcap-review");
        var sessions = findings.Single(f => f.Name == "kcap-sessions");
        await Assert.That(review.Issue).IsEqualTo(McpRegistrationIssue.CanonicalDuplicate);
        await Assert.That(sessions.Issue).IsEqualTo(McpRegistrationIssue.Conflict); // different path → preserved
    }

    [Test]
    public async Task Remove_strips_only_canonical_duplicates_and_preserves_the_rest() {
        var json = """
        {
          "keep": 1,
          "mcpServers": {
            "kcap-flows": { "command": "kcap", "args": ["mcp","flows"] },
            "kcap-memory": { "command": "kcap", "args": ["mcp","memory"], "env": { "X": "1" } },
            "other": { "command": "npx" }
          },
          "projects": { "/w/repo": { "mcpServers": {
            "kcap-review": { "command": "kcap", "args": ["mcp","review"] } } } }
        }
        """;

        var result = McpRegistrationAudit.RemoveClaudeDuplicates(json);
        var root = (JsonObject)JsonNode.Parse(result)!;

        await Assert.That((int)root["keep"]!).IsEqualTo(1);
        var servers = (JsonObject)root["mcpServers"]!;
        await Assert.That(servers.ContainsKey("kcap-flows")).IsFalse();   // canonical → removed
        await Assert.That(servers.ContainsKey("kcap-memory")).IsTrue();   // conflict (custom env) → preserved
        await Assert.That(servers.ContainsKey("other")).IsTrue();         // foreign → preserved

        var projectServers = (JsonObject)root["projects"]!["/w/repo"]!["mcpServers"]!;
        await Assert.That(projectServers.ContainsKey("kcap-review")).IsFalse(); // project scope cleaned too
    }

    [Test]
    public async Task Remove_returns_input_unchanged_when_unparseable() {
        await Assert.That(McpRegistrationAudit.RemoveClaudeDuplicates("{ not json")).IsEqualTo("{ not json");
    }

    [Test]
    public async Task IsCanonicalKcapEntry_rejects_unknown_names_and_non_objects() {
        var canonical = JsonNode.Parse("""{ "command": "kcap", "args": ["mcp","flows"] }""");
        await Assert.That(McpRegistrationAudit.IsCanonicalKcapEntry("my-flows", canonical, null)).IsFalse();
        await Assert.That(McpRegistrationAudit.IsCanonicalKcapEntry("kcap-flows", JsonNode.Parse("\"str\""), null)).IsFalse();
        await Assert.That(McpRegistrationAudit.IsCanonicalKcapEntry("kcap-flows", null, null)).IsFalse();
        await Assert.That(McpRegistrationAudit.IsCanonicalKcapEntry(null, canonical, null)).IsFalse();
    }

    [Test]
    public async Task FindAbsoluteKcapCommands_extracts_string_and_argv_shapes_only_for_kcap_names() {
        var json = """
        { "mcpServers": {
            "kcap-review":   { "command": "/opt/kcap/bin/kcap", "args": ["mcp","review"] },
            "kcap-sessions": { "command": "kcap", "args": ["mcp","sessions"] },
            "other":         { "command": "/opt/kcap/bin/kcap" } } }
        """;

        var abs = McpRegistrationAudit.FindAbsoluteKcapCommands(json);
        await Assert.That(abs.Count).IsEqualTo(1);
        await Assert.That(abs[0].Name).IsEqualTo("kcap-review");
        await Assert.That(abs[0].Command).IsEqualTo("/opt/kcap/bin/kcap");

        // OpenCode's argv-array shape under its "mcp" block.
        var opencode = """{ "mcp": { "kcap-review": { "command": ["/opt/kcap/bin/kcap", "mcp", "review"] } } }""";
        var absArgv = McpRegistrationAudit.FindAbsoluteKcapCommands(opencode, blockKey: "mcp");
        await Assert.That(absArgv.Count).IsEqualTo(1);
        await Assert.That(absArgv[0].Command).IsEqualTo("/opt/kcap/bin/kcap");
    }
}
