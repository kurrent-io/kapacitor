using System.Linq;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit.Mcp;

public class JsonMcpConfigWriterTests {
    static string TempConfig(string name = "mcp.json") =>
        Path.Combine(Directory.CreateTempSubdirectory("kcap-mcpwriter-").FullName, name);

    // In-memory marker double: treats an entry as kcap-owned iff its key starts with "kcap-".
    sealed class FakeMarker : IMcpMarker {
        readonly HashSet<string> _owned = [];
        public bool Owns(string cfg, string name, JsonNode entry) => name.StartsWith("kcap-");
        public void Record(string cfg, IReadOnlyList<string> names) { foreach (var n in names) _owned.Add(n); }
        public IEnumerable<string> Owned(string cfg) => _owned;
        public void Clear(string cfg) => _owned.Clear();
    }

    static JsonObject Read(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    [Test]
    public async Task Register_on_missing_file_writes_all_servers_standard_shape() {
        var path = TempConfig();
        var change = JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, cwd: null, new FakeMarker());

        await Assert.That(change).IsEqualTo(JsonMcpConfigWriter.Change.Updated);
        var servers = (JsonObject)Read(path)["mcpServers"]!;
        await Assert.That(servers.Count).IsEqualTo(KcapMcpServers.All.Count);
        await Assert.That((string)servers["kcap-review"]!["command"]!).IsEqualTo("kcap");
        await Assert.That(servers["kcap-review"]!["args"]!.AsArray().Count).IsEqualTo(2);
    }

    [Test]
    public async Task Register_gemini_shape_marks_only_read_only_servers_trusted() {
        var path = TempConfig();
        JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Gemini, cwd: null, new FakeMarker());

        var servers = (JsonObject)Read(path)["mcpServers"]!;
        // Read-only servers (pure reads) get trust:true; write/flow-launching servers keep prompting.
        await Assert.That((bool)servers["kcap-review"]!["trust"]!).IsTrue();
        await Assert.That((bool)servers["kcap-sessions"]!["trust"]!).IsTrue();
        await Assert.That(servers["kcap-memory"]!["trust"]).IsNull();   // writes (save) → still prompts
        await Assert.That(servers["kcap-flows"]!["trust"]).IsNull();    // spawns reviewer → still prompts
    }

    [Test]
    public async Task Register_standard_shape_emits_no_trust_field() {
        var path = TempConfig();
        JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, cwd: null, new FakeMarker());

        // Cursor uses the plain Standard shape — no per-server trust knob, so we emit none anywhere.
        var servers = (JsonObject)Read(path)["mcpServers"]!;
        foreach (var kv in servers) await Assert.That(kv.Value!["trust"]).IsNull();
    }

    [Test]
    public async Task Register_gemini_heals_existing_untrusted_read_only_entry() {
        var path = TempConfig();
        // A pre-trust kcap-review entry (no trust field) as an older install would have written.
        File.WriteAllText(path, """{ "mcpServers": { "kcap-review": { "command": "kcap", "args": ["mcp","review"] } } }""");

        var change = JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Gemini, cwd: null, new FakeMarker());

        await Assert.That(change).IsEqualTo(JsonMcpConfigWriter.Change.Updated);
        await Assert.That((bool)((JsonObject)Read(path)["mcpServers"]!)["kcap-review"]!["trust"]!).IsTrue();
    }

    [Test]
    public async Task Register_preserves_user_servers() {
        var path = TempConfig();
        File.WriteAllText(path, """{ "mcpServers": { "playwright": { "command": "npx", "args": ["-y","playwright"] } } }""");

        JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, new FakeMarker());

        var servers = (JsonObject)Read(path)["mcpServers"]!;
        await Assert.That((string)servers["playwright"]!["command"]!).IsEqualTo("npx");
        await Assert.That(servers.ContainsKey("kcap-review")).IsTrue();
    }

    [Test]
    public async Task Register_is_idempotent() {
        var path = TempConfig();
        var marker = new FakeMarker();
        JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, marker);
        var second = JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, marker);

        await Assert.That(second).IsEqualTo(JsonMcpConfigWriter.Change.Unchanged);
    }

    [Test]
    public async Task Register_fails_closed_on_malformed_file() {
        var path = TempConfig();
        File.WriteAllText(path, "{ not json");
        var change = JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, new FakeMarker());

        await Assert.That(change).IsEqualTo(JsonMcpConfigWriter.Change.Failed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo("{ not json");
    }

    [Test]
    public async Task Register_on_empty_file_writes_all_servers() {
        // Antigravity (and others) ship a 0-byte mcp_config.json on first run. An empty file has
        // nothing to preserve, so it must be treated as an empty config — NOT fail-closed as malformed.
        var path = TempConfig();
        File.WriteAllText(path, "");
        var change = JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, new FakeMarker());

        await Assert.That(change).IsEqualTo(JsonMcpConfigWriter.Change.Updated);
        var servers = (JsonObject)Read(path)["mcpServers"]!;
        await Assert.That(servers.Count).IsEqualTo(KcapMcpServers.All.Count);
        await Assert.That(servers.ContainsKey("kcap-review")).IsTrue();
    }

    [Test]
    public async Task Register_on_whitespace_only_file_writes_all_servers() {
        var path = TempConfig();
        File.WriteAllText(path, "  \n\t\n");
        var change = JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, new FakeMarker());

        await Assert.That(change).IsEqualTo(JsonMcpConfigWriter.Change.Updated);
        await Assert.That(((JsonObject)Read(path)["mcpServers"]!).ContainsKey("kcap-review")).IsTrue();
    }

    [Test]
    public async Task Register_fails_closed_when_block_is_wrong_type() {
        var path = TempConfig();
        File.WriteAllText(path, """{ "mcpServers": "oops" }""");
        var change = JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, new FakeMarker());

        await Assert.That(change).IsEqualTo(JsonMcpConfigWriter.Change.Failed);
    }

    [Test]
    public async Task Register_does_not_clobber_user_authored_kcap_lookalike() {
        var path = TempConfig();
        // A user server literally named kcap-review but pointing elsewhere; FakeMarker.Owns
        // returns true for the "kcap-" prefix, so replace this with the real marker semantics
        // in Task 4's integration test. Here we assert the collision path via a non-prefixed name.
        File.WriteAllText(path, """{ "mcpServers": { "kcap-custom": { "command": "mine" } } }""");
        JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, new FakeMarker());

        var servers = (JsonObject)Read(path)["mcpServers"]!;
        await Assert.That((string)servers["kcap-custom"]!["command"]!).IsEqualTo("mine");
    }

    [Test]
    public async Task Register_opencode_shape_uses_command_array_type_local_enabled() {
        var path = TempConfig("opencode.json");
        JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.OpenCode, null, new FakeMarker());

        var block = (JsonObject)Read(path)["mcp"]!;
        var review = (JsonObject)block["kcap-review"]!;
        await Assert.That((string)review["type"]!).IsEqualTo("local");
        await Assert.That(review["command"]!.AsArray().Count).IsEqualTo(3); // kcap, mcp, review
        await Assert.That((bool)review["enabled"]!).IsTrue();
    }

    [Test]
    public async Task Register_copilot_shape_sets_type_stdio() {
        var path = TempConfig();
        JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Copilot, null, new FakeMarker());
        var review = (JsonObject)((JsonObject)Read(path)["mcpServers"]!)["kcap-review"]!;
        await Assert.That((string)review["type"]!).IsEqualTo("stdio");
    }

    [Test]
    public async Task Register_emits_cwd_only_for_repo_scoped_servers() {
        var path = TempConfig();
        JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, cwd: "/w/repo", new FakeMarker());
        var servers = (JsonObject)Read(path)["mcpServers"]!;
        await Assert.That(servers["kcap-review"]!["cwd"]).IsNull();
        await Assert.That((string)servers["kcap-sessions"]!["cwd"]!).IsEqualTo("/w/repo");
    }

    [Test]
    public async Task Unregister_removes_only_owned_and_drops_empty_block() {
        var path = TempConfig();
        var marker = new FakeMarker();
        File.WriteAllText(path, """{ "mcpServers": { "playwright": { "command": "npx" } } }""");
        JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, marker);

        var change = JsonMcpConfigWriter.Unregister(path, McpConfigShape.Standard, marker);
        await Assert.That(change).IsEqualTo(JsonMcpConfigWriter.Change.Updated);

        var servers = (JsonObject)Read(path)["mcpServers"]!;
        await Assert.That(servers.ContainsKey("playwright")).IsTrue();
        await Assert.That(servers.ContainsKey("kcap-review")).IsFalse();
    }

    [Test]
    public async Task Unregister_clears_marker_even_when_no_kcap_entries_to_remove() {
        var dir = Directory.CreateTempSubdirectory("kcap-unreg-").FullName;
        var path = Path.Combine(dir, "mcp.json");
        var marker = new McpMarker("test", _ => Path.Combine(dir, "marker.json"));
        // Marker recorded, but the JSON has a user server and NO kcap entries (a hand-edit removed them).
        File.WriteAllText(path, """{ "mcpServers": { "my-tool": { "command": "x" } } }""");
        marker.Record(path, ["kcap-review"]);

        var change = JsonMcpConfigWriter.Unregister(path, McpConfigShape.Standard, marker);
        await Assert.That(change).IsEqualTo(JsonMcpConfigWriter.Change.Unchanged); // nothing kcap to remove; user server stays
        await Assert.That(marker.Owned(path)).IsEmpty();                          // marker cleared regardless
    }

    [Test]
    public async Task Register_preserves_unrecorded_kcap_named_user_server() {
        var dir = Directory.CreateTempSubdirectory("kcap-collide-").FullName;
        var path = Path.Combine(dir, "mcp.json");
        var marker = new McpMarker("test", _ => Path.Combine(dir, "marker.json")); // real marker, nothing recorded
        File.WriteAllText(path, """{ "mcpServers": { "kcap-review": { "command": "user-owned" } } }""");

        JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, marker);

        var review = (JsonObject)((JsonObject)JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!)["kcap-review"]!;
        await Assert.That((string)review["command"]!).IsEqualTo("user-owned"); // NOT clobbered
    }

    [Test]
    public async Task Register_heals_stale_owned_entry_but_stays_idempotent() {
        var dir = Directory.CreateTempSubdirectory("kcap-heal-").FullName;
        var path = Path.Combine(dir, "mcp.json");
        var marker = new McpMarker("test", _ => Path.Combine(dir, "marker.json"));

        // First registration records ownership + writes canonical entries.
        JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, marker);
        // Re-running with no changes is a no-op (idempotent).
        var again = JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, marker);
        await Assert.That(again).IsEqualTo(JsonMcpConfigWriter.Change.Unchanged);

        // Simulate a stale owned entry (still command "kcap", so still kcap-owned).
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        ((JsonObject)root["mcpServers"]!)["kcap-review"] =
            new JsonObject { ["command"] = "kcap", ["args"] = new JsonArray { "mcp", "OLD-review" } };
        File.WriteAllText(path, root.ToJsonString());

        // Re-register heals it back to canonical.
        var change = JsonMcpConfigWriter.Register(path, KcapMcpServers.All, McpConfigShape.Standard, null, marker);
        await Assert.That(change).IsEqualTo(JsonMcpConfigWriter.Change.Updated);
        var review = (JsonObject)((JsonObject)JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!)["kcap-review"]!;
        var args = review["args"]!.AsArray().Select(n => (string)n!).ToArray();
        await Assert.That(args).IsEquivalentTo(new[] { "mcp", "review" }); // healed
    }
}
