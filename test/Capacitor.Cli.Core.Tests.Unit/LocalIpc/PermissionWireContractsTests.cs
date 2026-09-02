using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class PermissionWireContractsTests {
    static JsonElement El(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

    [Test]
    public async Task Pending_dto_roundtrips_and_writes_snake_case_with_nulls_and_flags() {
        var dto = new PermissionPendingDto("r1", "a1", "s1", "claude", "Bash", El("""{"command":"ls"}"""), null, false, true, "2026-08-28T10:00:00.0000000+00:00");
        var json = JsonSerializer.Serialize(dto, PermissionIpcJsonContext.Default.PermissionPendingDto);
        await Assert.That(json).Contains("\"request_id\":\"r1\"");
        await Assert.That(json).Contains("\"tool_input\":{\"command\":\"ls\"}");
        await Assert.That(json).Contains("\"suggestions\":null");
        await Assert.That(json).Contains("\"suggestions_omitted\":true");
        var back = JsonSerializer.Deserialize(json, PermissionIpcJsonContext.Default.PermissionPendingDto)!;
        await Assert.That(back.RequestId).IsEqualTo("r1");
        await Assert.That(back.ToolInput!.Value.GetProperty("command").GetString()).IsEqualTo("ls");
        await Assert.That(back.SuggestionsOmitted).IsTrue();
    }

    [Test]
    public async Task Empty_object_decodes_to_nulls_and_false_flags() {
        var dto = JsonSerializer.Deserialize("{}", PermissionIpcJsonContext.Default.PermissionPendingDto)!;
        await Assert.That(dto.RequestId).IsNull();
        await Assert.That(dto.ToolInput).IsNull();
        await Assert.That(dto.ToolInputOmitted).IsFalse();
        await Assert.That(PermissionWire.IsPendingStructurallyValid(dto)).IsFalse();
    }

    [Test]
    public async Task Structural_validity_requires_ids_vendor_and_time_but_not_tool_name() {
        var ok = new PermissionPendingDto("r1", "a1", "s1", "codex", "", null, null, false, false, "t");
        await Assert.That(PermissionWire.IsPendingStructurallyValid(ok)).IsTrue();
        await Assert.That(PermissionWire.IsPendingStructurallyValid(ok with { AgentId = "" })).IsFalse();
        await Assert.That(PermissionWire.IsPendingStructurallyValid(ok with { SessionId = "" })).IsFalse();
        await Assert.That(PermissionWire.IsPendingStructurallyValid(ok with { RequestedAt = "" })).IsFalse();
    }

    [Test]
    public async Task Resolve_resolved_and_ack_dtos_roundtrip() {
        var resolve = new PermissionResolveDto("r1", "allow", El("""[{"type":"toolAlwaysAllow","tool":"Bash"}]"""), null);
        var rjson = JsonSerializer.Serialize(resolve, PermissionIpcJsonContext.Default.PermissionResolveDto);
        await Assert.That(rjson).Contains("\"apply_permissions\":[{\"type\":\"toolAlwaysAllow\",\"tool\":\"Bash\"}]");
        await Assert.That(rjson).Contains("\"updated_input\":null");

        var resolved = new PermissionResolvedDto("r1", "deny", "agent_gone");
        var sjson = JsonSerializer.Serialize(resolved, PermissionIpcJsonContext.Default.PermissionResolvedDto);
        await Assert.That(sjson).IsEqualTo("{\"request_id\":\"r1\",\"outcome\":\"deny\",\"source\":\"agent_gone\"}");

        var ack = JsonSerializer.Deserialize("{\"ok\":false,\"error\":\"x\"}", PermissionIpcJsonContext.Default.PermissionAckDto)!;
        await Assert.That(ack.Ok).IsFalse();
        await Assert.That(ack.Error).IsEqualTo("x");
    }

    [Test]
    [Arguments("6BA7B810-9DAD-11D1-80B4-00C04FD430C8", "6ba7b8109dad11d180b400c04fd430c8")]
    [Arguments("6ba7b8109dad11d180b400c04fd430c8", "6ba7b8109dad11d180b400c04fd430c8")]
    [Arguments("6BA7B8109DAD11D180B400C04FD430C8", "6ba7b8109dad11d180b400c04fd430c8")]
    [Arguments("not-a-guid", null)]
    [Arguments("", null)]
    [Arguments(null, null)]
    public async Task Canonical_parses_any_guid_shape_to_n_form(string? input, string? expected) {
        await Assert.That(PermissionWire.Canonical(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task Pending_dto_carries_an_optional_tool_use_id_and_decodes_without_it() {
        var dto = new PermissionPendingDto("r1", "a1", "s1", "claude", "Bash", null, null, false, false, "t", "toolu_01ABC");
        var json = JsonSerializer.Serialize(dto, PermissionIpcJsonContext.Default.PermissionPendingDto);
        await Assert.That(json).Contains("\"tool_use_id\":\"toolu_01ABC\"");
        var back = JsonSerializer.Deserialize(json, PermissionIpcJsonContext.Default.PermissionPendingDto)!;
        await Assert.That(back.ToolUseId).IsEqualTo("toolu_01ABC");

        var older = JsonSerializer.Deserialize(
            """{"request_id":"r1","agent_id":"a1","session_id":"s1","vendor":"claude","tool_name":"Bash","requested_at":"t"}""",
            PermissionIpcJsonContext.Default.PermissionPendingDto)!;
        await Assert.That(older.ToolUseId).IsNull();
        await Assert.That(PermissionWire.IsPendingStructurallyValid(older)).IsTrue();
    }

    [Test]
    public async Task Worst_case_pending_frame_writes_and_reads_under_the_codec_cap() {
        var name = new string('"', PermissionWire.MaxToolNameBytes);               // every byte escapes to \"
        var key  = new string('\\', PermissionWire.MaxAgentIdBytes);               // every byte escapes to \\
        var big  = "\"" + new string('x', PermissionWire.MaxElementBytes - 2) + "\"";
        var id   = new string('"', PermissionWire.MaxToolUseIdBytes);               // every byte escapes to \"
        var dto  = new PermissionPendingDto("r1", key, "s1", "claude", name, El(big), El(big), false, false, "t", id);
        var json = JsonSerializer.Serialize(dto, PermissionIpcJsonContext.Default.PermissionPendingDto);
        await Assert.That(Encoding.UTF8.GetByteCount(json) < FrameCodec.MaxPayload).IsTrue();

        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.PermissionJson(FrameType.PermissionPending, json), CancellationToken.None);
        ms.Position = 0;
        var back = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(back!.Text).IsEqualTo(json);
    }

    [Test]
    public async Task Decision_record_writes_snake_case() {
        var rec = new PermissionDecisionRecord("t", "a1", "s1", "claude", "Bash", "allow", "app");
        var json = JsonSerializer.Serialize(rec, PermissionDecisionJsonContext.Default.PermissionDecisionRecord);
        await Assert.That(json).IsEqualTo("{\"decided_at\":\"t\",\"agent_id\":\"a1\",\"session_id\":\"s1\",\"vendor\":\"claude\",\"tool_name\":\"Bash\",\"outcome\":\"allow\",\"source\":\"app\"}");
    }
}
