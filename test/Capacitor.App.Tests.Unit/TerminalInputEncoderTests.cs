using System.Text;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class TerminalInputEncoderTests {
    [Test]
    public async Task Paste_wraps_in_bracketed_paste_normalizes_crlf_and_drops_one_trailing_newline() {
        await Assert.That(Encoding.UTF8.GetString(TerminalInputEncoder.Paste("hi"))).IsEqualTo("\x1b[200~hi\x1b[201~");
        await Assert.That(Encoding.UTF8.GetString(TerminalInputEncoder.Paste("a\r\nb\n"))).IsEqualTo("\x1b[200~a\nb\x1b[201~");
        await Assert.That(Encoding.UTF8.GetString(TerminalInputEncoder.Paste("a\n\n"))).IsEqualTo("\x1b[200~a\n\x1b[201~");
        await Assert.That(TerminalInputEncoder.Submit).IsEquivalentTo("\r"u8.ToArray());
    }
}
