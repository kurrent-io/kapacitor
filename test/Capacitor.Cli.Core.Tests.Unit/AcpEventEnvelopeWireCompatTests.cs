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
            ToolResult:        "ok",
            ToolIsError:       true,
            Model:             "claude-opus-4-8",
            Cwd:               "/repo",
            RawSessionId:      "raw-sess-1",
            SessionMode:       "agent",
            EndReason:         "completed",
            ContextUsedTokens:   142_000,
            ContextWindowTokens: 200_000,
            TimestampIso:      "2026-07-08T00:00:00Z"
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

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AcpEventEnvelope);
        await Assert.That(back.Seq).IsEqualTo(7L);
        await Assert.That(back.Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(back.ToolInputJson).IsEqualTo("""{"command":"ls"}""");
        await Assert.That(back.ToolIsError).IsTrue();
        await Assert.That(back.ContextUsedTokens).IsEqualTo(142_000L);
        await Assert.That(back.ContextWindowTokens).IsEqualTo(200_000L);
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
}
