using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class RestartFrameCodecTests {
    [Test]
    public async Task Restart_frame_round_trips_mode() {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.Restart("when-idle"), default);
        ms.Position = 0;
        var f = await FrameCodec.ReadAsync(ms, default);

        await Assert.That(f).IsNotNull();
        await Assert.That(f!.Type).IsEqualTo(FrameType.Restart);
        await Assert.That(f.Text).IsEqualTo("when-idle");
    }

    [Test]
    public async Task RestartAck_frame_round_trips_status() {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.RestartAck("queued"), default);
        ms.Position = 0;
        var f = await FrameCodec.ReadAsync(ms, default);

        await Assert.That(f!.Type).IsEqualTo(FrameType.RestartAck);
        await Assert.That(f.Text).IsEqualTo("queued");
    }
}
