namespace Capacitor.App.Services;

using System.Text;

/// One incremental UTF-8 stream per attach attempt: PTY frames split multibyte
/// code points at arbitrary boundaries, and the terminal control's Feed does a
/// fresh GetString per call — this is the single decoder spanning the snapshot
/// and every live frame, flushed only at terminal completion.
public sealed class Utf8StreamDecoder {
    readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    public string Decode(ReadOnlySpan<byte> bytes) {
        if (bytes.IsEmpty) return "";
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var n = _decoder.GetChars(bytes, chars, flush: false);
        return new string(chars, 0, n);
    }

    public string Flush() {
        var chars = new char[4];
        var n = _decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
        return new string(chars, 0, n);
    }
}
