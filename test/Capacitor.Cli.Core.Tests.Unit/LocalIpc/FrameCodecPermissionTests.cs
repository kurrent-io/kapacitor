using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class FrameCodecPermissionTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, CancellationToken.None);
        ms.Position = 0;
        return (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
    }

    [Test]
    [Arguments(FrameType.PermissionSubscribe)]
    [Arguments(FrameType.PermissionResolve)]
    [Arguments(FrameType.PermissionPending)]
    [Arguments(FrameType.PermissionResolved)]
    [Arguments(FrameType.PermissionAck)]
    public async Task Permission_frames_roundtrip_with_text_payload(FrameType type) {
        var f = await RoundTrip(LocalFrame.PermissionJson(type, """{"k":"v"}"""));
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Subscribe_frame_roundtrips_with_empty_payload() {
        var f = await RoundTrip(new LocalFrame(FrameType.PermissionSubscribe));
        await Assert.That(f.Type).IsEqualTo(FrameType.PermissionSubscribe);
        await Assert.That(f.Text).IsEqualTo("");
    }

    [Test]
    public async Task Permission_frame_values_are_stable_wire_bytes() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.PermissionSubscribe).IsEqualTo((byte)20);
        await Assert.That((byte)FrameType.PermissionResolve).IsEqualTo((byte)21);
        await Assert.That((byte)FrameType.PermissionPending).IsEqualTo((byte)77);
        await Assert.That((byte)FrameType.PermissionResolved).IsEqualTo((byte)78);
        await Assert.That((byte)FrameType.PermissionAck).IsEqualTo((byte)79);
#pragma warning restore TUnitAssertions0005
    }

    [Test]
    public async Task Max_payload_is_eight_mebibytes() {
#pragma warning disable TUnitAssertions0005
        await Assert.That(FrameCodec.MaxPayload).IsEqualTo(8 * 1024 * 1024);
#pragma warning restore TUnitAssertions0005
    }
}
