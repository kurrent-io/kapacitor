using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// Pure unit tests for <see cref="AcpDebugFrameLog.Cap"/>, the shared
/// length cap used by every <c>KCAP_ACP_DEBUG_FRAMES</c> call site (the translator's unknown-kind
/// dump, <c>AcpChildProcess</c>'s stderr drain, <c>AcpConnection</c>'s full-frame logging). Those
/// call sites are covered by their own test files; this isolates the cap logic itself.
/// </summary>
public class AcpDebugFrameLogTests {
    [Test]
    public async Task Cap_ShortContent_ReturnsItUnchanged() {
        var result = AcpDebugFrameLog.Cap("short content");

        await Assert.That(result).IsEqualTo("short content");
    }

    [Test]
    public async Task Cap_EmptyContent_ReturnsEmpty() {
        var result = AcpDebugFrameLog.Cap("");

        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    public async Task Cap_ContentOverTheLimit_TruncatesAndAnnotatesHowMuchWasCut() {
        var huge = new string('x', 10_000);

        var result = AcpDebugFrameLog.Cap(huge);

        await Assert.That(result.Length).IsLessThan(huge.Length);
        await Assert.That(result).Contains("truncated");
        await Assert.That(result).Contains("5904"); // 10_000 - 4096
    }

    [Test]
    public async Task Cap_ContentExactlyAtTheLimit_ReturnsItUnchanged() {
        var exact = new string('y', 4096);

        var result = AcpDebugFrameLog.Cap(exact);

        await Assert.That(result).IsEqualTo(exact);
    }
}
