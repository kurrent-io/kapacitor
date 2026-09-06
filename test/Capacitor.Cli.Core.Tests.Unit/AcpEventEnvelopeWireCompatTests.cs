using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Option B task 1 wire-compat guard: <see cref="AcpEventEnvelope"/>/<see cref="AcpBatchAck"/>
/// are daemon-local mirrors of the server's <c>Capacitor.Server.Core.Acp.AcpEventEnvelope</c> /
/// <c>AcpBatchAck</c> (read from the ai-686 server worktree,
/// <c>src/Capacitor.Server.Core/Acp/AcpEventEnvelope.cs</c>) — they cross the SignalR wire to the
/// server's <c>CapacitorHub.AcpSessionStarted</c>/<c>AcpSessionEvents</c> hub methods, so a
/// field-name/casing mismatch would silently break the wire. The server has NO explicit
/// <c>[JsonPropertyName]</c> attributes on either type — every property rides the wire under its
/// SignalR JSON protocol's <c>JsonNamingPolicy.SnakeCaseLower</c> (see server
/// <c>Program.cs</c>'s <c>AddSignalR().AddJsonProtocol(...)</c>), exactly the same naming policy this
/// context's <see cref="CapacitorJsonContext"/> is configured with (see the
/// <c>AcpInteractionRequest_round_trips_and_uses_snake_case_server_contract_wire_shape</c> precedent
/// in <c>AcpInteractionMessagesTests</c>). This test locks in every expected snake_case property name
/// so a rename here (or there) fails loudly instead of silently breaking ACP transcript forwarding.
/// </summary>
public class AcpEventEnvelopeWireCompatTests {
    [Test]
    public async Task AcpEventEnvelope_serializes_every_field_under_its_expected_snake_case_wire_name() {
        var env = new AcpEventEnvelope(
            ContractVersion:   1,
            Seq:               7,
            Kind:              AcpEventKind.ToolCall,
            Text:              "hello",
            ThinkingEncrypted: true,
            ToolCallId:        "call-1",
            ToolName:          "bash",
            ToolInputJson:     """{"command":"ls"}""",
            ToolKind:          AcpToolKind.Execute,
            ToolResult:        "ok",
            ToolIsError:       true,
            Model:             "claude-opus-4-8",
            Cwd:               "/repo",
            RawSessionId:      "raw-sess-1",
            SessionMode:       "agent",
            EndReason:         "completed",
            ContextUsedTokens:   142_000,
            ContextWindowTokens: 200_000,
            TimestampIso:      "2026-07-08T00:00:00Z",
            Ephemeral:         true,
            ItemId:            "item-42"
        );

        var json = JsonSerializer.Serialize(env, CapacitorJsonContext.Default.AcpEventEnvelope);

        // Every server-side property name, snake_cased — field-for-field per AcpEventEnvelope.cs.
        await Assert.That(json).Contains(@"""contract_version"":1");
        await Assert.That(json).Contains(@"""seq"":7");
        await Assert.That(json).Contains($@"""kind"":""{AcpEventKind.ToolCall}""");
        await Assert.That(json).Contains(@"""text"":""hello""");
        await Assert.That(json).Contains(@"""thinking_encrypted"":true");
        await Assert.That(json).Contains(@"""tool_call_id"":""call-1""");
        await Assert.That(json).Contains(@"""tool_name"":""bash""");
        await Assert.That(json).Contains(@"""tool_input_json""");
        await Assert.That(json).Contains(@"""tool_kind"":""execute""");
        await Assert.That(json).Contains(@"""tool_result"":""ok""");
        await Assert.That(json).Contains(@"""tool_is_error"":true");
        await Assert.That(json).Contains(@"""model"":""claude-opus-4-8""");
        await Assert.That(json).Contains(@"""cwd"":""/repo""");
        await Assert.That(json).Contains(@"""raw_session_id"":""raw-sess-1""");
        await Assert.That(json).Contains(@"""session_mode"":""agent""");
        await Assert.That(json).Contains(@"""end_reason"":""completed""");
        await Assert.That(json).Contains(@"""context_used_tokens"":142000");
        await Assert.That(json).Contains(@"""context_window_tokens"":200000");
        await Assert.That(json).Contains(@"""timestamp_iso"":""2026-07-08T00:00:00Z""");
        await Assert.That(json).Contains(@"""ephemeral"":true");
        await Assert.That(json).Contains(@"""item_id"":""item-42""");

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AcpEventEnvelope);
        await Assert.That(back.Seq).IsEqualTo(7L);
        await Assert.That(back.Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(back.ToolInputJson).IsEqualTo("""{"command":"ls"}""");
        await Assert.That(back.ToolIsError).IsTrue();
        await Assert.That(back.ContextUsedTokens).IsEqualTo(142_000L);
        await Assert.That(back.ContextWindowTokens).IsEqualTo(200_000L);
        await Assert.That(back.Ephemeral).IsTrue();
        await Assert.That(back.ItemId).IsEqualTo("item-42");
        await Assert.That(back.ToolKind).IsEqualTo(AcpToolKind.Execute);
    }

    [Test]
    public async Task AcpToolKind_constants_match_the_ACP_tool_kind_vocabulary() {
        await Assert.That(AcpToolKind.Read).IsEqualTo("read");
        await Assert.That(AcpToolKind.Edit).IsEqualTo("edit");
        await Assert.That(AcpToolKind.Delete).IsEqualTo("delete");
        await Assert.That(AcpToolKind.Move).IsEqualTo("move");
        await Assert.That(AcpToolKind.Search).IsEqualTo("search");
        await Assert.That(AcpToolKind.Execute).IsEqualTo("execute");
        await Assert.That(AcpToolKind.Think).IsEqualTo("think");
        await Assert.That(AcpToolKind.Fetch).IsEqualTo("fetch");
        await Assert.That(AcpToolKind.SwitchMode).IsEqualTo("switch_mode");
        await Assert.That(AcpToolKind.Other).IsEqualTo("other");
    }

    /// The closed set is what lets a consumer switch on ten tokens. An absent kind stays absent —
    /// "no lane classified this" is a different answer from "none of the above".
    [Test]
    public async Task Normalize_keeps_the_vocabulary_closed_and_absence_distinguishable() {
        foreach (var known in new[] { "read", "edit", "delete", "move", "search", "execute", "think", "fetch", "switch_mode", "other" })
            await Assert.That(AcpToolKind.Normalize(known)).IsEqualTo(known).Because(known);

        await Assert.That(AcpToolKind.Normalize("Read")).IsEqualTo(AcpToolKind.Other);
        await Assert.That(AcpToolKind.Normalize("summarise")).IsEqualTo(AcpToolKind.Other);
        await Assert.That(AcpToolKind.Normalize(null)).IsNull();
        await Assert.That(AcpToolKind.Normalize("")).IsNull();
        await Assert.That(AcpToolKind.Normalize("  ")).IsNull();
    }

    [Test]
    public async Task AcpEventEnvelope_defaults_the_ephemeral_lane_fields_to_canonical() {
        var env = new AcpEventEnvelope(Seq: 4, Kind: AcpEventKind.AssistantText, Text: "hi");

        await Assert.That(env.Ephemeral).IsFalse();
        await Assert.That(env.ItemId).IsNull();

        var json = JsonSerializer.Serialize(env, CapacitorJsonContext.Default.AcpEventEnvelope);
        await Assert.That(json).Contains(@"""ephemeral"":false");
    }

    [Test]
    public async Task AcpEventEnvelope_defaults_ContractVersion_to_1_and_omits_nothing_unexpected() {
        // Mirrors the server's `public int ContractVersion { get; init; } = 1;` default exactly —
        // a translator that forgets to stamp ContractVersion must still produce a valid v1 envelope.
        var env = new AcpEventEnvelope(Seq: 0, Kind: AcpEventKind.SessionStarted);

        await Assert.That(env.ContractVersion).IsEqualTo(1);

        var json = JsonSerializer.Serialize(env, CapacitorJsonContext.Default.AcpEventEnvelope);
        await Assert.That(json).Contains(@"""contract_version"":1");
    }

    [Test]
    public async Task AcpEventKind_constants_match_the_server_contracts_wire_values() {
        // Field-for-field against Capacitor.Server.Core.Acp.AcpEventKind's string constants.
        await Assert.That(AcpEventKind.SessionStarted).IsEqualTo("session_started");
        await Assert.That(AcpEventKind.UserMessage).IsEqualTo("user_message");
        await Assert.That(AcpEventKind.AssistantText).IsEqualTo("assistant_text");
        await Assert.That(AcpEventKind.AssistantThinking).IsEqualTo("assistant_thinking");
        await Assert.That(AcpEventKind.ToolCall).IsEqualTo("tool_call");
        await Assert.That(AcpEventKind.ToolResult).IsEqualTo("tool_result");
        await Assert.That(AcpEventKind.SessionTitle).IsEqualTo("session_title");
        await Assert.That(AcpEventKind.SessionEnded).IsEqualTo("session_ended");
        await Assert.That(AcpEventKind.Usage).IsEqualTo("usage");
        await Assert.That(AcpEventKind.SystemNote).IsEqualTo("system_note");
        await Assert.That(AcpEventKind.Plan).IsEqualTo("plan");
        await Assert.That(AcpEventKind.TokenUsage).IsEqualTo("token_usage");
    }

    [Test]
    public async Task TokenUsage_envelope_carries_every_additive_bucket_under_its_snake_case_name() {
        // The additive-billing delta lane (§2.4). Distinct from the context-occupancy Usage kind —
        // the server stamps these into $usage metadata. Field-for-field vs the server mirror.
        var env = new AcpEventEnvelope(
            Kind:                       AcpEventKind.TokenUsage,
            Model:                      "gpt-5-codex",
            UsageInputTokens:           1200,
            UsageCachedInputTokens:     300,
            UsageCacheWriteInputTokens: 64,
            UsageOutputTokens:          450,
            UsageReasoningTokens:       128);

        var json = JsonSerializer.Serialize(env, CapacitorJsonContext.Default.AcpEventEnvelope);
        await Assert.That(json).Contains(@"""kind"":""token_usage""");
        await Assert.That(json).Contains(@"""usage_input_tokens"":1200");
        await Assert.That(json).Contains(@"""usage_cached_input_tokens"":300");
        await Assert.That(json).Contains(@"""usage_cache_write_input_tokens"":64");
        await Assert.That(json).Contains(@"""usage_output_tokens"":450");
        await Assert.That(json).Contains(@"""usage_reasoning_tokens"":128");

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AcpEventEnvelope);
        await Assert.That(back.UsageInputTokens).IsEqualTo(1200L);
        await Assert.That(back.UsageCachedInputTokens).IsEqualTo(300L);
        await Assert.That(back.UsageCacheWriteInputTokens).IsEqualTo(64L);
        await Assert.That(back.UsageOutputTokens).IsEqualTo(450L);
        await Assert.That(back.UsageReasoningTokens).IsEqualTo(128L);
        await Assert.That(back.Model).IsEqualTo("gpt-5-codex");
    }

    [Test]
    public async Task AcpBatchAck_round_trips_a_gap_reject_shape_with_snake_case_field_names() {
        // Mirrors the server's `AcpBatchAck(long AcceptedSeq, long PersistedSeq, long? ExpectedNextSeq = null)`
        // — a gap-reject ack sets ExpectedNextSeq (the daemon's resend cursor per §2.3).
        const string serverShapedJson = """{"accepted_seq":4,"persisted_seq":4,"expected_next_seq":5}""";

        var ack = JsonSerializer.Deserialize(serverShapedJson, CapacitorJsonContext.Default.AcpBatchAck);

        await Assert.That(ack.AcceptedSeq).IsEqualTo(4L);
        await Assert.That(ack.PersistedSeq).IsEqualTo(4L);
        await Assert.That(ack.ExpectedNextSeq).IsEqualTo(5L);

        var json = JsonSerializer.Serialize(ack, CapacitorJsonContext.Default.AcpBatchAck);
        await Assert.That(json).Contains(@"""accepted_seq"":4");
        await Assert.That(json).Contains(@"""persisted_seq"":4");
        await Assert.That(json).Contains(@"""expected_next_seq"":5");
    }

    [Test]
    public async Task AcpBatchAck_success_ack_has_null_ExpectedNextSeq() {
        var ack  = new AcpBatchAck(AcceptedSeq: 10, PersistedSeq: 10);
        var json = JsonSerializer.Serialize(ack, CapacitorJsonContext.Default.AcpBatchAck);

        await Assert.That(ack.ExpectedNextSeq).IsNull();
        await Assert.That(json).Contains(@"""accepted_seq"":10");
        await Assert.That(json).Contains(@"""expected_next_seq"":null");
    }

    // ── Source-claim / confirm outcomes (mirror the server's AcpSessionSourceClaim / ConfirmSessionLaunch) ──
    // These are RETURNED BY the server hub methods, so the daemon only ever DESERIALIZES them (never
    // sends them). The contract is therefore: the daemon must decode the exact shape the server emits —
    // snake_case properties + camelCase enum values (JsonNamingPolicy.CamelCase on the server's
    // JsonStringEnumConverter). JsonStringEnumConverter deserialization is case-insensitive, so the
    // server's camelCase maps onto the daemon's members; these tests lock that in against the real
    // server wire strings.

    [Test]
    public async Task AcpSourceClaimOutcome_deserializes_the_servers_wire_shape() {
        // Exactly what the server's AcpSessionSourceClaim emits for a Bound claim.
        var bound = JsonSerializer.Deserialize(
            @"{""outcome"":""bound"",""ownership_token"":3,""accepted_seq"":-1}",
            CapacitorJsonContext.Default.AcpSourceClaimOutcome);
        await Assert.That(bound.Outcome).IsEqualTo(AcpBindOutcome.Bound);
        await Assert.That(bound.OwnershipToken).IsEqualTo(3L);
        await Assert.That(bound.AcceptedSeq).IsEqualTo(-1L);

        var rejected = JsonSerializer.Deserialize(
            @"{""outcome"":""rejected"",""ownership_token"":0,""accepted_seq"":0}",
            CapacitorJsonContext.Default.AcpSourceClaimOutcome);
        await Assert.That(rejected.Outcome).IsEqualTo(AcpBindOutcome.Rejected);
    }

    [Test]
    public async Task AcpLaunchConfirmOutcome_deserializes_every_server_camelCase_value() {
        // Including the multi-word member, which rides the wire as "alreadyConfirmed" — the one most
        // likely to silently mismatch a naming policy.
        await Assert.That(Decode(@"""confirmed""")).IsEqualTo(AcpLaunchConfirmOutcome.Confirmed);
        await Assert.That(Decode(@"""alreadyConfirmed""")).IsEqualTo(AcpLaunchConfirmOutcome.AlreadyConfirmed);
        await Assert.That(Decode(@"""superseded""")).IsEqualTo(AcpLaunchConfirmOutcome.Superseded);
        await Assert.That(Decode(@"""notFound""")).IsEqualTo(AcpLaunchConfirmOutcome.NotFound);

        static AcpLaunchConfirmOutcome Decode(string json) =>
            JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AcpLaunchConfirmOutcome);
    }
}
