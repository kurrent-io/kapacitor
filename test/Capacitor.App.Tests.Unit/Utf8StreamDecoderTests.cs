using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class Utf8StreamDecoderTests {
    [Test]
    [Arguments("é")]      // 2-byte
    [Arguments("€")]      // 3-byte
    [Arguments("𝄞")]      // 4-byte
    public async Task Multibyte_characters_split_at_every_boundary_reassemble(string ch) {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"a{ch}b");
        for (var split = 1; split < bytes.Length; split++) {
            var decoder = new Utf8StreamDecoder();
            var text = decoder.Decode(bytes.AsSpan(0, split)) + decoder.Decode(bytes.AsSpan(split)) + decoder.Flush();
            await Assert.That(text).IsEqualTo($"a{ch}b");
        }
    }

    [Test]
    public async Task The_snapshot_live_seam_is_one_stream() {
        var bytes = System.Text.Encoding.UTF8.GetBytes("€");
        var decoder = new Utf8StreamDecoder();
        var snapshotPart = decoder.Decode(bytes.AsSpan(0, 1));   // snapshot ends mid-character
        var livePart = decoder.Decode(bytes.AsSpan(1));          // first live frame completes it
        await Assert.That(snapshotPart + livePart).IsEqualTo("€");
    }

    [Test]
    public async Task Flush_emits_a_replacement_for_a_dangling_partial() {
        var decoder = new Utf8StreamDecoder();
        decoder.Decode(System.Text.Encoding.UTF8.GetBytes("€").AsSpan(0, 1));
        await Assert.That(decoder.Flush()).IsEqualTo("�");
    }
}
