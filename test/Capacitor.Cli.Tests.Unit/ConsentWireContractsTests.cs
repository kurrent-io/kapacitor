using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit;

/// Wire-shape pins for the consent v2 contracts (spec §4.1, §4.4): trailing nullable
/// members with no C# default, nulls always written, snake_case names byte-compatible with
/// existing daemons' output.
public class ConsentWireContractsTests {
    [Test]
    public async Task Pending_dto_roundtrips_with_trailing_fields() {
        var dto = new ConsentPendingDto("a1", "github:1", "agent", "/r", "codex", "2026-08-08T10:00:00.0000000+00:00", 45, "Mathias", "p1");
        var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPendingDto);
        await Assert.That(json).Contains("\"requester_display\":\"Mathias\"");
        await Assert.That(json).Contains("\"prompt_id\":\"p1\"");
        var back = JsonSerializer.Deserialize(json, ConsentIpcJsonContext.Default.ConsentPendingDto);
        await Assert.That(back).IsEqualTo(dto);
    }

    [Test]
    public async Task Pending_dto_from_v1_daemon_reads_null_display_and_prompt_id() {
        // A pre-v2 daemon's exact serialization (no requester_display, no prompt_id).
        const string v1 = "{\"request_id\":\"a1\",\"requester\":\"github:1\",\"kind\":\"agent\",\"repo_path\":\"/r\",\"vendor\":\"codex\",\"requested_at\":\"t\",\"timeout_seconds\":45}";
        var dto = JsonSerializer.Deserialize(v1, ConsentIpcJsonContext.Default.ConsentPendingDto)!;
        await Assert.That(dto.RequesterDisplay).IsNull();
        await Assert.That(dto.PromptId).IsNull();
    }

    [Test]
    public async Task Resolve_dto_carries_prompt_id_and_ack_carries_rule_saved_with_nulls_written() {
        var resolve = new ConsentResolveDto("a1", "allow", null, "p1");
        var rjson = JsonSerializer.Serialize(resolve, ConsentIpcJsonContext.Default.ConsentResolveDto);
        await Assert.That(rjson).Contains("\"prompt_id\":\"p1\"");

        var ack = new ConsentAckDto(true, null, null);
        var ajson = JsonSerializer.Serialize(ack, ConsentIpcJsonContext.Default.ConsentAckDto);
        await Assert.That(ajson).Contains("\"rule_saved\":null"); // nulls-always-written convention

        // Old-format ack (no rule_saved member) → null, not an error.
        var old = JsonSerializer.Deserialize("{\"ok\":false,\"error\":\"x\"}", ConsentIpcJsonContext.Default.ConsentAckDto)!;
        await Assert.That(old.RuleSaved).IsNull();
    }

    [Test]
    public async Task Decision_record_matches_existing_on_disk_field_names_verbatim() {
        var rec = new ConsentDecisionRecord("t", "a1", "github:1", false, "agent", "/r", "codex", "allowed", "rule[0]", null);
        var json = JsonSerializer.Serialize(rec, ConsentDecisionJsonContext.Default.ConsentDecisionRecord);
        foreach (var name in new[] { "\"decided_at\"", "\"agent_id\"", "\"requester\"", "\"requester_is_owner\"",
                                     "\"kind\"", "\"repo_path\"", "\"vendor\"", "\"outcome\"", "\"source\"", "\"requester_display\"" })
            await Assert.That(json).Contains(name);

        // Old log line (pre-v2, no requester_display) parses; missing bool reads false.
        const string oldLine = "{\"decided_at\":\"t\",\"agent_id\":\"a1\",\"requester\":null,\"requester_is_owner\":true,\"kind\":\"agent\",\"repo_path\":\"/r\",\"vendor\":\"codex\",\"outcome\":\"allowed\",\"source\":\"owner\"}";
        var back = JsonSerializer.Deserialize(oldLine, ConsentDecisionJsonContext.Default.ConsentDecisionRecord)!;
        await Assert.That(back.RequesterDisplay).IsNull();
        await Assert.That(back.RequesterIsOwner).IsTrue();
    }

    [Test]
    public async Task V2_frame_values_are_pinned_and_codec_roundtrips_them() {
        await Assert.That((byte)FrameType.ConsentSubscribeV2).IsEqualTo((byte)17);
        await Assert.That((byte)FrameType.ConsentResolveV2).IsEqualTo((byte)18);

        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.ConsentJson(FrameType.ConsentResolveV2, "{\"x\":1}"), CancellationToken.None);
        await FrameCodec.WriteAsync(ms, new LocalFrame(FrameType.ConsentSubscribeV2), CancellationToken.None);
        ms.Position = 0;
        var f1 = (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
        var f2 = (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
        await Assert.That(f1.Type).IsEqualTo(FrameType.ConsentResolveV2);
        await Assert.That(f1.Text).IsEqualTo("{\"x\":1}");
        await Assert.That(f2.Type).IsEqualTo(FrameType.ConsentSubscribeV2);
    }
}
