namespace Capacitor.Cli.Core.Tests.Unit.Acp;

using System.Text.Json;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Policy;

/// The vendor-neutral ACP tool-call mapping: a kind the policy vocabulary covers becomes that
/// canonical kind, and anything unmapped or missing a required field becomes Other rather than
/// escaping evaluation.
public class AcpActionNormalizerTests {
    static JsonElement ToolCall(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public async Task Execute_with_a_command_maps_to_shell() {
        var a = AcpActionNormalizer.Normalize(
            ToolCall("""{"kind":"execute","rawInput":{"command":"git status"},"toolCallId":"tc1"}"""), "cursor", null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Shell);
        await Assert.That(a.Analyzed).IsTrue();
        await Assert.That(a.Vendor).IsEqualTo("cursor");
    }

    [Test]
    public async Task Edit_takes_paths_from_locations() {
        var a = AcpActionNormalizer.Normalize(
            ToolCall("""{"kind":"edit","locations":[{"path":"/wt/a.cs"},{"path":"/wt/b.cs"}]}"""), "cursor", null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.FileEdit);
        await Assert.That(a.Paths).IsEquivalentTo(new[] { "/wt/a.cs", "/wt/b.cs" });
    }

    [Test]
    public async Task Fetch_maps_to_network() {
        var a = AcpActionNormalizer.Normalize(
            ToolCall("""{"kind":"fetch","rawInput":{"url":"https://example.com/x"}}"""), "gemini", null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Network);
        await Assert.That(a.Host).IsEqualTo("example.com");
    }

    /// A URL that omits the port still carries the scheme default, so `port: 443` reaches both
    /// spellings instead of only the explicit one.
    [Test]
    public async Task Fetch_carries_the_scheme_default_port() {
        var implicitPort = AcpActionNormalizer.Normalize(
            ToolCall("""{"kind":"fetch","rawInput":{"url":"https://example.com/x"}}"""), "gemini", null);
        await Assert.That(implicitPort.Port).IsEqualTo(443);
        var explicitPort = AcpActionNormalizer.Normalize(
            ToolCall("""{"kind":"fetch","rawInput":{"url":"https://example.com:443/x"}}"""), "gemini", null);
        await Assert.That(explicitPort.Port).IsEqualTo(443);
        var nonDefault = AcpActionNormalizer.Normalize(
            ToolCall("""{"kind":"fetch","rawInput":{"url":"https://example.com:8443/x"}}"""), "gemini", null);
        await Assert.That(nonDefault.Port).IsEqualTo(8443);
    }

    [Test]
    public async Task Unknown_or_incomplete_tool_calls_fall_to_other() {
        var noCommand = AcpActionNormalizer.Normalize(ToolCall("""{"kind":"execute"}"""), "cursor", null);
        await Assert.That(noCommand.Kind).IsEqualTo(ActionKind.Other);
        await Assert.That(noCommand.RawToolName).IsEqualTo("execute");
        var unknown = AcpActionNormalizer.Normalize(ToolCall("""{"kind":"other","title":"Weird"}"""), "cursor", null);
        await Assert.That(unknown.Kind).IsEqualTo(ActionKind.Other);
    }

    [Test]
    public async Task Read_falls_back_to_the_raw_input_path() {
        var a = AcpActionNormalizer.Normalize(
            ToolCall("""{"kind":"read","rawInput":{"path":"/wt/a.cs"}}"""), "cursor", null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.FileRead);
        await Assert.That(a.Paths).IsEquivalentTo(new[] { "/wt/a.cs" });
    }

    [Test]
    public async Task A_relative_path_resolves_against_the_cwd() {
        var a = AcpActionNormalizer.Normalize(
            ToolCall("""{"kind":"edit","locations":[{"path":"src/a.cs"}]}"""), "cursor", "/wt");
        await Assert.That(a.Kind).IsEqualTo(ActionKind.FileEdit);
        await Assert.That(a.Paths).IsEquivalentTo(new[] { "/wt/src/a.cs" });
    }

    [Test]
    public async Task A_kind_less_frame_names_its_title_as_the_raw_tool() {
        var a = AcpActionNormalizer.Normalize(ToolCall("""{"title":"Weird","toolCallId":"tc1"}"""), "kiro", null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Other);
        await Assert.That(a.RawToolName).IsEqualTo("Weird");
    }

    [Test]
    public async Task A_non_object_tool_call_is_other_and_never_throws() {
        var a = AcpActionNormalizer.Normalize(ToolCall("\"just a string\""), "cursor", null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Other);
        await Assert.That(a.RawToolName).IsNull();
        await Assert.That(a.Vendor).IsEqualTo("cursor");
    }
}
