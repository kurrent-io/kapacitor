using Capacitor.Cli.Core.LocalIpc;
using System.Text.Json;

namespace Capacitor.Cli.Tests.Unit;

public class FrameCodecHelloTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, CancellationToken.None);
        ms.Position = 0;
        return (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
    }

    [Test]
    [Arguments(FrameType.Hello)]
    [Arguments(FrameType.HelloReply)]
    public async Task Hello_frames_roundtrip_with_text_payload(FrameType type) {
        var f = await RoundTrip(new LocalFrame(type) { Text = """{"k":"v"}""" });
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Hello_frame_with_empty_payload() {
        var f = await RoundTrip(new LocalFrame(FrameType.Hello) { Text = "" });
        await Assert.That(f.Type).IsEqualTo(FrameType.Hello);
        await Assert.That(f.Text).IsEqualTo("");
    }

    [Test]
    public async Task HelloReply_with_full_dto() {
        var dto = new HelloReplyDto(
            ProtocolVersion: 1,
            DaemonVersion: "x",
            DaemonName: "n",
            Capabilities: new List<string> { "consent/1" },
            Pid: 4242,
            InstanceId: "inst-abc");
        var json = JsonSerializer.Serialize(dto, HelloIpcJsonContext.Default.HelloReplyDto);
        var f = new LocalFrame(FrameType.HelloReply) { Text = json };
        var rt = await RoundTrip(f);
        await Assert.That(rt.Type).IsEqualTo(FrameType.HelloReply);
        await Assert.That(rt.Text).IsEqualTo("""{"protocol_version":1,"daemon_version":"x","daemon_name":"n","capabilities":["consent/1"],"pid":4242,"instance_id":"inst-abc"}""");
    }

    [Test]
    public async Task ClientHello_with_null_fields() {
        var dto = new ClientHelloDto(ClientName: null, ClientVersion: null);
        var json = JsonSerializer.Serialize(dto, HelloIpcJsonContext.Default.ClientHelloDto);
        var f = new LocalFrame(FrameType.Hello) { Text = json };
        var rt = await RoundTrip(f);
        await Assert.That(rt.Type).IsEqualTo(FrameType.Hello);
        await Assert.That(rt.Text).IsEqualTo("""{"client_name":null,"client_version":null}""");
    }

    [Test]
    public async Task HelloReply_forward_compat_with_unknown_property() {
        var json = """{"protocol_version":1,"daemon_version":"x","daemon_name":"n","capabilities":["consent/1"],"unknown_field":"ignored"}""";
        var dto = JsonSerializer.Deserialize(json, HelloIpcJsonContext.Default.HelloReplyDto);
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.ProtocolVersion).IsEqualTo(1);
        await Assert.That(dto.DaemonVersion).IsEqualTo("x");
        await Assert.That(dto.DaemonName).IsEqualTo("n");
        await Assert.That(dto.Capabilities).IsNotNull();
        await Assert.That(dto.Capabilities!.Count).IsEqualTo(1);
        await Assert.That(dto.Capabilities[0]).IsEqualTo("consent/1");
    }

    [Test]
    public async Task HelloReply_with_omitted_capabilities_deserializes_to_null_and_is_treated_as_empty() {
        // An older daemon's reply might not carry "capabilities" at all — STJ leaves the absent
        // member at its default (null) rather than throwing. A client must be able to read this
        // without an NRE and treat the absence as "no capabilities advertised".
        var json = """{"protocol_version":1,"daemon_version":"x","daemon_name":"n"}""";
        var dto = JsonSerializer.Deserialize(json, HelloIpcJsonContext.Default.HelloReplyDto);
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.ProtocolVersion).IsEqualTo(1);
        await Assert.That(dto.Capabilities).IsNull();
        await Assert.That((dto.Capabilities ?? []).Count).IsEqualTo(0); // the client-side "treat as empty" idiom
    }

    [Test]
    public async Task Hello_frame_values_are_stable_wire_bytes() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.Hello).IsEqualTo((byte)15);
        await Assert.That((byte)FrameType.HelloReply).IsEqualTo((byte)75);
#pragma warning restore TUnitAssertions0005
    }
}
