// test/Capacitor.Cli.Tests.Unit/Acp/AcpRpcTests.cs
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

public class AcpRpcTests {
    [Test]
    public async Task AcpRequest_serializes_jsonrpc_envelope_and_round_trips() {
        var paramsJson = JsonDocument.Parse("""{"protocolVersion":1}""").RootElement;
        var request    = new AcpRequest(1, "initialize", paramsJson);

        var json = JsonSerializer.Serialize(request, CapacitorJsonContext.Default.AcpRequest);

        await Assert.That(json).Contains(@"""jsonrpc"":""2.0""");
        await Assert.That(json).Contains(@"""id"":1");
        await Assert.That(json).Contains(@"""method"":""initialize""");
        await Assert.That(json).Contains(@"""params"":{""protocolVersion"":1}");

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AcpRequest)!;
        await Assert.That(back.Id).IsEqualTo(request.Id);
        await Assert.That(back.Method).IsEqualTo(request.Method);
    }

    /// <summary>Outbound notifications carry the envelope too. The other two frame types had this
    /// covered; this one did not, and every AcpNotification test only ever DESERIALIZED an inbound
    /// frame — so nothing would have caught the jsonrpc property silently dropping out of the
    /// serialized form (which is what marking it static, or any change STJ declines to serialize,
    /// would do).</summary>
    [Test]
    public async Task AcpNotification_serializes_the_jsonrpc_envelope() {
        var notification = new AcpNotification(
            "session/cancel",
            JsonDocument.Parse("""{"sessionId":"s-1"}""").RootElement
        );

        var json = JsonSerializer.Serialize(notification, CapacitorJsonContext.Default.AcpNotification);

        await Assert.That(json).Contains(@"""jsonrpc"":""2.0""");
        await Assert.That(json).Contains(@"""method"":""session/cancel""");
        await Assert.That(json).DoesNotContain(@"""id""");
    }

    [Test]
    public async Task AcpRequest_omits_params_key_when_null() {
        var request = new AcpRequest(2, "session/cancel", null);

        var json = JsonSerializer.Serialize(request, CapacitorJsonContext.Default.AcpRequest);

        await Assert.That(json).DoesNotContain(@"""params""");
    }

    [Test]
    public async Task AcpNotification_deserializes_session_update() {
        const string line = """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"abc","update":{"sessionUpdate":"agent_message_chunk"}}}""";

        var notification = JsonSerializer.Deserialize(line, CapacitorJsonContext.Default.AcpNotification)!;

        await Assert.That(notification.Method).IsEqualTo("session/update");
        await Assert.That(notification.Params).IsNotNull();
        await Assert.That(notification.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo("abc");
    }

    [Test]
    public async Task AcpResponse_deserializes_result_with_null_error() {
        const string line = """{"jsonrpc":"2.0","id":1,"result":{"stopReason":"end_turn"}}""";

        var response = JsonSerializer.Deserialize(line, CapacitorJsonContext.Default.AcpResponse)!;

        await Assert.That(response.Id).IsEqualTo(1L);
        await Assert.That(response.Result).IsNotNull();
        await Assert.That(response.Result!.Value.GetProperty("stopReason").GetString()).IsEqualTo("end_turn");
        await Assert.That(response.Error).IsNull();
    }

    [Test]
    public async Task AcpResponse_deserializes_error_with_null_result() {
        const string line = """{"jsonrpc":"2.0","id":3,"error":{"code":-32603,"message":"Internal error","data":[{"expected":"string"}]}}""";

        var response = JsonSerializer.Deserialize(line, CapacitorJsonContext.Default.AcpResponse)!;

        await Assert.That(response.Id).IsEqualTo(3L);
        await Assert.That(response.Result).IsNull();
        await Assert.That(response.Error).IsNotNull();
        await Assert.That(response.Error!.Code).IsEqualTo(-32603);
        await Assert.That(response.Error!.Message).IsEqualTo("Internal error");
        await Assert.That(response.Error!.Data).IsNotNull();
    }

    [Test]
    public async Task AcpResponse_serializes_result_without_error_key() {
        var resultJson = JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement;
        var response   = new AcpResponse(1, resultJson, null);

        var json = JsonSerializer.Serialize(response, CapacitorJsonContext.Default.AcpResponse);

        await Assert.That(json).Contains(@"""jsonrpc"":""2.0""");
        await Assert.That(json).Contains(@"""result""");
        await Assert.That(json).DoesNotContain(@"""error""");
    }

    [Test]
    public async Task AcpResponse_serializes_error_without_result_key() {
        var error    = new AcpError(-32601, "Method not found", null);
        var response = new AcpResponse(2, null, error);

        var json = JsonSerializer.Serialize(response, CapacitorJsonContext.Default.AcpResponse);

        await Assert.That(json).Contains(@"""jsonrpc"":""2.0""");
        await Assert.That(json).Contains(@"""error""");
        await Assert.That(json).Contains(@"""code"":-32601");
        await Assert.That(json).Contains(@"""message"":""Method not found""");
        await Assert.That(json).DoesNotContain(@"""result""");
        await Assert.That(json).DoesNotContain(@"""data""");
    }
}
