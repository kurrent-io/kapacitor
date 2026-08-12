using Capacitor.Cli;
using Capacitor.Cli.Core.Cursor;
using Capacitor.Cli.Core.OpenCode;
using Capacitor.Cli.Core.Pi;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit;

public class WorkItemsNudgeEmitterTests {
    [Test]
    public async Task Build_returns_null_for_missing_session_id() {
        await Assert.That(WorkItemsNudgeEmitter.Build(null)).IsNull();
        await Assert.That(WorkItemsNudgeEmitter.Build("")).IsNull();
        await Assert.That(WorkItemsNudgeEmitter.Build("   ")).IsNull();
    }

    [Test]
    public async Task Build_returns_null_for_an_oversized_session_id() {
        await Assert.That(WorkItemsNudgeEmitter.Build(new string('a', 257))).IsNull();
    }

    [Test]
    public async Task Build_suppresses_a_session_id_with_a_backtick_or_control_char() {
        await Assert.That(WorkItemsNudgeEmitter.Build("abc`rm -rf`")).IsNull();
        await Assert.That(WorkItemsNudgeEmitter.Build("abc\ndef")).IsNull();
        await Assert.That(WorkItemsNudgeEmitter.Build("abc\tdef")).IsNull();
    }

    [Test]
    public async Task Build_accepts_a_file_path_session_id() {
        // Pi's session id is a file path — slashes/dots/colons are safe inside the code span.
        var nudge = WorkItemsNudgeEmitter.Build("/home/u/.pi/sessions/2026-08-12T10:00.jsonl");
        await Assert.That(nudge).IsNotNull();
        await Assert.That(nudge!).Contains("/home/u/.pi/sessions/2026-08-12T10:00.jsonl");
    }

    [Test]
    public async Task Build_renders_the_session_id_verbatim() {
        var nudge = WorkItemsNudgeEmitter.Build("abc-123-DEF");
        await Assert.That(nudge).IsNotNull();
        await Assert.That(nudge!).Contains("`abc-123-DEF`");
    }

    [Test]
    public async Task Build_names_all_three_declare_tools_and_the_relation_directionality() {
        var nudge = WorkItemsNudgeEmitter.Build("s1")!;
        await Assert.That(nudge).Contains("declare_work_item");
        await Assert.That(nudge).Contains("declare_work_breakdown");
        await Assert.That(nudge).Contains("declare_work_relation");
        await Assert.That(nudge).Contains("blocks");
        await Assert.That(nudge).Contains("blocked_by");
        // create-by-title only when there is no tracker item, no id fabrication
        await Assert.That(nudge).Contains("by title");
        await Assert.That(nudge).Contains("never invent an id");
    }

    [Test]
    public async Task Resolve_returns_null_when_opted_out() {
        // Claude is always available, so opt-out is the only suppressor here.
        await Assert.That(WorkItemsNudgeEmitter.Resolve(SessionStartHarness.Claude, "s1", optedOut: true)).IsNull();
    }

    [Test]
    public async Task Resolve_returns_the_nudge_for_an_available_harness() {
        var nudge = WorkItemsNudgeEmitter.Resolve(SessionStartHarness.Claude, "s1", optedOut: false);
        await Assert.That(nudge).IsNotNull();
        await Assert.That(nudge!).Contains("`s1`");
    }

    [Test]
    public async Task Resolve_returns_null_when_the_harness_lacks_the_server() {
        // Codex with an empty temp config → workitems not registered → suppressed.
        var dir = Directory.CreateTempSubdirectory("kcap-nudge-codex-").FullName;
        var codexConfig = Path.Combine(dir, "config.toml");
        await File.WriteAllTextAsync(codexConfig, "model = \"gpt-5-codex\"\n");
        await Assert.That(
            WorkItemsNudgeEmitter.Resolve(SessionStartHarness.Codex, "s1", optedOut: false, codexConfigPath: codexConfig))
            .IsNull();
    }
}

public class WorkItemsNudgeAvailabilityTests {
    static string NewHome(out string root) {
        root = Directory.CreateTempSubdirectory("kcap-nudge-avail-").FullName;
        return root;
    }

    [Test]
    public async Task Claude_is_always_available() {
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Claude)).IsTrue();
    }

    [Test]
    public async Task Cursor_present_entry_is_available() {
        var home = NewHome(out _);
        var path = CursorPaths.UserMcpJson(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"mcpServers":{"kcap-workitems":{"command":"kcap","args":["mcp","workitems"]}}}""");
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Cursor, home)).IsTrue();
    }

    [Test]
    public async Task Cursor_absent_entry_suppresses() {
        var home = NewHome(out _);
        var path = CursorPaths.UserMcpJson(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"mcpServers":{"kcap-review":{"command":"kcap"}}}""");
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Cursor, home)).IsFalse();
    }

    [Test]
    public async Task Missing_config_file_suppresses() {
        var home = NewHome(out _);
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Cursor, home)).IsFalse();
    }

    [Test]
    public async Task Malformed_config_suppresses() {
        var home = NewHome(out _);
        var path = CursorPaths.UserMcpJson(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ this is not json ");
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Cursor, home)).IsFalse();
    }

    [Test]
    public async Task OpenCode_disabled_entry_suppresses() {
        var home = NewHome(out _);
        var path = OpenCodePaths.McpConfigJson(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // OpenCode's block key is `mcp`; an explicit "enabled": false reads as absent.
        await File.WriteAllTextAsync(path, """{"mcp":{"kcap-workitems":{"type":"local","enabled":false}}}""");
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.OpenCode, home)).IsFalse();
    }

    [Test]
    public async Task Cursor_non_object_entry_suppresses() {
        // A null / string / array value for the key is malformed → fail closed.
        foreach (var badValue in new[] { "null", "\"kcap\"", "[1,2]" }) {
            var home = NewHome(out _);
            var path = CursorPaths.UserMcpJson(home);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"mcpServers\":{\"kcap-workitems\":" + badValue + "}}");
            await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Cursor, home))
                .IsFalse().Because($"value {badValue} is malformed");
        }
    }

    [Test]
    public async Task Cursor_non_boolean_enabled_suppresses() {
        var home = NewHome(out _);
        var path = CursorPaths.UserMcpJson(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A string "false" (or any non-Boolean) enabled value must not read as enabled.
        await File.WriteAllTextAsync(path, """{"mcpServers":{"kcap-workitems":{"command":"kcap","enabled":"false"}}}""");
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Cursor, home)).IsFalse();
    }

    [Test]
    public async Task Pi_commented_declaration_before_the_real_one_suppresses() {
        var home = NewHome(out _);
        var path = PiPaths.KcapMcpExtension(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A commented-out declaration mentioning workitems precedes the REAL declaration, which omits
        // it — comment stripping must make the real one win.
        await File.WriteAllTextAsync(path,
            "// const KCAP_MCP_SERVERS = [\"workitems\"]\nconst KCAP_MCP_SERVERS = [\"review\", \"sessions\"];");
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Pi, home)).IsFalse();
    }

    [Test]
    public async Task Pi_block_commented_workitems_suppresses() {
        var home = NewHome(out _);
        var path = PiPaths.KcapMcpExtension(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            "/* KCAP_MCP_SERVERS = [\"workitems\"] */ const KCAP_MCP_SERVERS = [\"review\"];");
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Pi, home)).IsFalse();
    }

    [Test]
    public async Task Pi_non_exact_element_suppresses() {
        var home = NewHome(out _);
        var path = PiPaths.KcapMcpExtension(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // "workitems" only as a substring of another element must not count.
        await File.WriteAllTextAsync(path, "const KCAP_MCP_SERVERS = [\"review\", \"workitems-extra\"];");
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Pi, home)).IsFalse();
    }

    [Test]
    public async Task OpenCode_enabled_entry_is_available() {
        var home = NewHome(out _);
        var path = OpenCodePaths.McpConfigJson(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"mcp":{"kcap-workitems":{"type":"local","enabled":true}}}""");
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.OpenCode, home)).IsTrue();
    }

    [Test]
    public async Task Pi_extension_with_workitems_is_available() {
        var home = NewHome(out _);
        var path = PiPaths.KcapMcpExtension(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """const KCAP_MCP_SERVERS = ["review", "sessions", "flows", "memory", "analytics", "workitems"];""");
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Pi, home)).IsTrue();
    }

    [Test]
    public async Task Pi_extension_without_workitems_suppresses() {
        var home = NewHome(out _);
        var path = PiPaths.KcapMcpExtension(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """const KCAP_MCP_SERVERS = ["review", "sessions", "flows", "memory", "analytics"];""");
        await Assert.That(WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Pi, home)).IsFalse();
    }

    [Test]
    public async Task Codex_config_with_workitems_is_available() {
        var dir = Directory.CreateTempSubdirectory("kcap-nudge-codex2-").FullName;
        var codexConfig = Path.Combine(dir, "config.toml");
        await File.WriteAllTextAsync(codexConfig,
            "[mcp_servers.kcap-workitems]\ncommand = \"kcap\"\nargs = [\"mcp\", \"workitems\"]\n");
        await Assert.That(
            WorkItemsNudgeAvailability.IsRegisteredFor(SessionStartHarness.Codex, codexConfigPath: codexConfig)).IsTrue();
    }
}

public class WorkItemsNudgeRenderCompositionTests {
    const string Nudge = "## Work items\nnudge body";

    [Test]
    public async Task Nudge_only_opens_with_the_shared_marker() {
        // Pi renders the raw fragment, so the marker-first rule is observable in the output.
        var rendered = SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Pi, fragment: null, workItemsNudge: Nudge);
        await Assert.That(rendered).StartsWith(MemoryIndexEmitter.FragmentMarker);
        await Assert.That(rendered).Contains("nudge body");
    }

    [Test]
    public async Task Fragment_and_nudge_are_both_present_with_the_nudge_after() {
        var fragment = MemoryIndexEmitter.FragmentMarker + "\n## Team memory\nmem body";
        var rendered = SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Pi, fragment, Nudge);
        await Assert.That(rendered).Contains("mem body");
        await Assert.That(rendered).Contains("nudge body");
        await Assert.That(rendered.IndexOf("mem body", StringComparison.Ordinal))
            .IsLessThan(rendered.IndexOf("nudge body", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_null_nudge_is_byte_identical_to_the_pre_nudge_render() {
        // The isolation invariant at the render layer: passing no nudge changes nothing.
        var fragment = MemoryIndexEmitter.FragmentMarker + "\n## Team memory\nmem body";
        foreach (var harness in new[] {
                     SessionStartHarness.Codex, SessionStartHarness.Cursor, SessionStartHarness.Copilot,
                     SessionStartHarness.Gemini, SessionStartHarness.Kiro, SessionStartHarness.OpenCode,
                     SessionStartHarness.Pi, SessionStartHarness.Antigravity }) {
            var withNullNudge = SessionStartMemoryOutputAdapters.Render(harness, fragment, workItemsNudge: null);
            var baseline      = SessionStartMemoryOutputAdapters.Render(harness, fragment);
            await Assert.That(withNullNudge).IsEqualTo(baseline);

            var emptyBoth     = SessionStartMemoryOutputAdapters.Render(harness, fragment: null, workItemsNudge: null);
            var emptyBaseline = SessionStartMemoryOutputAdapters.Render(harness, fragment: null);
            await Assert.That(emptyBoth).IsEqualTo(emptyBaseline);
        }
    }
}
