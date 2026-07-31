using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit;

public class FrameCodecConsentTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, CancellationToken.None);
        ms.Position = 0;
        return (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
    }

    [Test]
    [Arguments(FrameType.ConsentSubscribe)]
    [Arguments(FrameType.ConsentResolve)]
    [Arguments(FrameType.ConsentRulesGet)]
    [Arguments(FrameType.ConsentRulesPut)]
    [Arguments(FrameType.ConsentPending)]
    [Arguments(FrameType.ConsentRules)]
    [Arguments(FrameType.ConsentAck)]
    public async Task Consent_frames_roundtrip_with_text_payload(FrameType type) {
        var f = await RoundTrip(new LocalFrame(type) { Text = """{"k":"v"}""" });
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Consent_frame_values_are_stable_wire_bytes() {
        await Assert.That((byte)FrameType.ConsentSubscribe).IsEqualTo((byte)11);
        await Assert.That((byte)FrameType.ConsentResolve).IsEqualTo((byte)12);
        await Assert.That((byte)FrameType.ConsentRulesGet).IsEqualTo((byte)13);
        await Assert.That((byte)FrameType.ConsentRulesPut).IsEqualTo((byte)14);
        await Assert.That((byte)FrameType.ConsentPending).IsEqualTo((byte)72);
        await Assert.That((byte)FrameType.ConsentRules).IsEqualTo((byte)73);
        await Assert.That((byte)FrameType.ConsentAck).IsEqualTo((byte)74);
    }
}
