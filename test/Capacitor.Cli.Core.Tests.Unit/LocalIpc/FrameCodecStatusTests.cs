using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// <summary>
/// Round-trips for the supervision frame pair. StatusSubscribe carries no payload
/// (Detach/List shape); DaemonStatus carries UTF-8 JSON in Text (consent-frame shape).
/// </summary>
public class FrameCodecStatusTests {
    [Test]
    public async Task StatusSubscribe_round_trips_with_an_empty_payload() {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, new LocalFrame(FrameType.StatusSubscribe), CancellationToken.None);
        ms.Position = 0;
        var f = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(f!.Type).IsEqualTo(FrameType.StatusSubscribe);
        await Assert.That(f.Text).IsEmpty();
        await Assert.That(f.Bytes).IsEmpty();
    }

    [Test]
    public async Task DaemonStatus_round_trips_its_json_text_payload() {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(
            ms, LocalFrame.StatusJson(FrameType.DaemonStatus, """{"daemon":{"name":"x"}}"""), CancellationToken.None);
        ms.Position = 0;
        var f = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(f!.Type).IsEqualTo(FrameType.DaemonStatus);
        await Assert.That(f.Text).IsEqualTo("""{"daemon":{"name":"x"}}""");
    }

    [Test]
    public async Task Frame_values_are_pinned_16_and_76() {
        // Append-only wire contract: these bytes are claimed by the spec and must never move.
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.StatusSubscribe).IsEqualTo((byte)16);
        await Assert.That((byte)FrameType.DaemonStatus).IsEqualTo((byte)76);
#pragma warning restore TUnitAssertions0005
    }
}
