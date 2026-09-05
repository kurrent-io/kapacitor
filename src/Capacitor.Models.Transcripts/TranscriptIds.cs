using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Capacitor.Models.Transcripts;

/// Every id a projection derives. The server's dedup set is keyed by these, so the bytes hashed
/// here are a persistence contract: a Guid contributes its own 16-byte layout, a string its UTF-8.
public static class TranscriptIds {
    public static Guid Hash(ReadOnlySpan<byte> bytes) => new(XxHash128.Hash(bytes));

    public static Guid Sibling(Guid primary, string suffix) {
        var suffixBytes = Encoding.UTF8.GetBytes(suffix);
        var input       = new byte[16 + suffixBytes.Length];
        primary.TryWriteBytes(input);
        suffixBytes.CopyTo(input, 16);
        return Hash(input);
    }

    public static Guid ClaudeFallback(int lineNumber, string line) =>
        Hash(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{lineNumber} {line}")));

    public static Guid ClaudeBlock(Guid recordId, int blockIndex) =>
        Sibling(recordId, string.Create(CultureInfo.InvariantCulture, $"block:{blockIndex}"));

    public static Guid ClaudeAttachment(string idScope, Guid recordId, int blockIndex) {
        var scopeBytes = Encoding.UTF8.GetBytes(idScope);
        var input      = new byte[scopeBytes.Length + 20];
        scopeBytes.CopyTo(input, 0);
        recordId.TryWriteBytes(input.AsSpan(scopeBytes.Length));
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(scopeBytes.Length + 16), blockIndex);
        return Hash(input);
    }

    public static Guid CodexRecord(string line) => Hash(Encoding.UTF8.GetBytes(line));
}
