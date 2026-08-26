namespace Capacitor.App.Services;

using System.Text;

/// One incremental UTF-8 stream per attach attempt: PTY frames split multibyte
/// code points at arbitrary boundaries, and the terminal control's Feed does a
/// fresh GetString per call — this is the single decoder spanning the snapshot
/// and every live frame, flushed only at terminal completion.
public sealed class Utf8StreamDecoder {
    readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    // Grow-only scratch, safe because delivery is strictly sequential (one reader pump):
    // without it every frame pays a second allocation beyond the result string.
    char[] _scratch = new char[256];

    public string Decode(ReadOnlySpan<byte> bytes) {
        if (bytes.IsEmpty) return "";
        var need = Encoding.UTF8.GetMaxCharCount(bytes.Length);
        if (_scratch.Length < need) _scratch = new char[need];
        var n = _decoder.GetChars(bytes, _scratch, flush: false);
        return new string(_scratch, 0, n);
    }

    public string Flush() {
        var chars = new char[4];
        var n = _decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
        return new string(chars, 0, n);
    }
}
