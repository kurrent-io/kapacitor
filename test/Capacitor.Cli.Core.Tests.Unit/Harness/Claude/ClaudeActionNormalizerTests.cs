namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;

public class ClaudeActionNormalizerTests {
    static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public async Task Bash_maps_to_shell_with_analysis() {
        var a = ClaudeActionNormalizer.Normalize("Bash", Input("""{"command":"git status"}"""), "/repo");
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Shell);
        await Assert.That(a.Analyzed).IsTrue();
        await Assert.That(a.Segments[0].Argv).IsEquivalentTo(new[] { "git", "status" });
        await Assert.That(a.Vendor).IsEqualTo("claude");
    }

    [Test]
    public async Task Bash_without_command_falls_to_other() {
        var a = ClaudeActionNormalizer.Normalize("Bash", Input("{}"), "/repo");
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Other);
        await Assert.That(a.RawToolName).IsEqualTo("Bash");
    }

    [Test]
    [Arguments("Edit", ActionKind.FileEdit)]
    [Arguments("Write", ActionKind.FileEdit)]
    [Arguments("MultiEdit", ActionKind.FileEdit)]
    [Arguments("Read", ActionKind.FileRead)]
    public async Task File_tools_resolve_file_path(string tool, ActionKind kind) {
        var a = ClaudeActionNormalizer.Normalize(tool, Input("""{"file_path":"src/x.cs"}"""), "/repo");
        await Assert.That(a.Kind).IsEqualTo(kind);
        await Assert.That(a.Paths).IsEquivalentTo(new[] { "/repo/src/x.cs" });
    }

    [Test]
    public async Task Grep_defaults_its_path_to_cwd() {
        var a = ClaudeActionNormalizer.Normalize("Grep", Input("""{"pattern":"x"}"""), "/repo");
        await Assert.That(a.Kind).IsEqualTo(ActionKind.FileRead);
        await Assert.That(a.Paths).IsEquivalentTo(new[] { "/repo" });
    }

    [Test]
    public async Task WebFetch_normalizes_the_host() {
        var a = ClaudeActionNormalizer.Normalize("WebFetch", Input("""{"url":"https://EXAMPLE.com:8443/x"}"""), null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Network);
        await Assert.That(a.Host).IsEqualTo("example.com");
        await Assert.That(a.Port).IsEqualTo(8443);
    }

    [Test]
    public async Task Mcp_tool_names_split_on_the_second_separator() {
        var a = ClaudeActionNormalizer.Normalize("mcp__kcap-flows__start_review_flow", Input("{}"), null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.McpTool);
        await Assert.That(a.Server).IsEqualTo("kcap-flows");
        await Assert.That(a.Tool).IsEqualTo("start_review_flow");
    }

    [Test]
    public async Task Unknown_tools_and_null_input_are_governable_as_other() {
        var a = ClaudeActionNormalizer.Normalize("TodoWrite", null, null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Other);
        await Assert.That(a.RawToolName).IsEqualTo("TodoWrite");
        var nameless = ClaudeActionNormalizer.Normalize(null, null, null);
        await Assert.That(nameless.Kind).IsEqualTo(ActionKind.Other);
        await Assert.That(PolicyComponents.RestrictionOf(nameless)).IsNotEmpty();
    }

    [Test]
    public async Task An_input_element_that_cannot_even_be_read_still_falls_back_without_throwing() {
        // The default JsonElement (no parent document) throws InvalidOperationException from
        // GetRawText — both the primary path and the catch handler's own Other() call hit it.
        JsonElement? unreadable = default(JsonElement);
        var a = ClaudeActionNormalizer.Normalize("Bash", unreadable, "/repo");
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Other);
        await Assert.That(a.Vendor).IsEqualTo("claude");
        await Assert.That(PolicyComponents.RestrictionOf(a)).IsNotEmpty();
    }
}
