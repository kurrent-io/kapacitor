using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Antigravity;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Antigravity;

/// <summary>
/// Unit tests for <see cref="AntigravityGenMetadata"/>: the empirically-pinned
/// protobuf field walk (top.1 → 4 → {2 input, 3 output, 5 cacheRead}; 19 model id, 21
/// label) and the synthetic USAGE line it produces. Uses hand-built protobuf vectors so
/// the decoder is verified without a real .db (the shapes match Antigravity 2.2.1 blobs).
/// </summary>
public class AntigravityGenMetadataTests {
    // ── tiny protobuf encoder ──
    static byte[] Varint(int field, long v) {
        var b = new List<byte>();
        WriteVarint(b, (ulong)(field << 3));       // wiretype 0
        WriteVarint(b, (ulong)v);
        return b.ToArray();
    }
    static byte[] Ld(int field, byte[] payload) {
        var b = new List<byte>();
        WriteVarint(b, (ulong)((field << 3) | 2)); // wiretype 2
        WriteVarint(b, (ulong)payload.Length);
        b.AddRange(payload);
        return b.ToArray();
    }
    static void WriteVarint(List<byte> b, ulong v) {
        do { var x = (byte)(v & 0x7f); v >>= 7; if (v != 0) x |= 0x80; b.Add(x); } while (v != 0);
    }
    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    static byte[] Blob(long input, long output, long? cacheRead, string? modelId, string? label) {
        var counts = cacheRead is { } cr
            ? Concat(Varint(2, input), Varint(3, output), Varint(5, cr))
            : Concat(Varint(2, input), Varint(3, output));
        var usageParts = new List<byte[]> { Ld(4, counts) };
        if (modelId is not null) usageParts.Add(Ld(19, Encoding.UTF8.GetBytes(modelId)));
        if (label   is not null) usageParts.Add(Ld(21, Encoding.UTF8.GetBytes(label)));
        return Ld(1, Concat(usageParts.ToArray()));
    }

    [Test]
    public async Task Decodes_tokens_and_model_id() {
        var row = AntigravityGenMetadata.TryDecode(
            Blob(input: 19360, output: 246, cacheRead: 16275, modelId: "gemini-default", label: "Gemini 3.5 Flash (Medium)"));

        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Value.InputTokens).IsEqualTo(19360L);
        await Assert.That(row.Value.OutputTokens).IsEqualTo(246L);
        await Assert.That(row.Value.CacheReadTokens).IsEqualTo(16275L);
        await Assert.That(row.Value.Model).IsEqualTo("gemini-default");
    }

    [Test]
    public async Task Absent_cache_read_is_zero_and_label_is_the_fallback_model() {
        var row = AntigravityGenMetadata.TryDecode(
            Blob(input: 19360, output: 246, cacheRead: null, modelId: null, label: "Gemini 3.5 Flash (Medium)"));

        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Value.CacheReadTokens).IsEqualTo(0L);
        await Assert.That(row.Value.Model).IsEqualTo("Gemini 3.5 Flash (Medium)");
    }

    [Test]
    public async Task Malformed_or_empty_blob_returns_null() {
        await Assert.That(AntigravityGenMetadata.TryDecode(ReadOnlySpan<byte>.Empty)).IsNull();
        await Assert.That(AntigravityGenMetadata.TryDecode(new byte[] { 0xff, 0xff, 0xff })).IsNull();
        // A well-formed message with zero tokens is not useful cost data → null.
        await Assert.That(AntigravityGenMetadata.TryDecode(Blob(0, 0, null, "m", null))).IsNull();
    }

    // an over-large varint must not surface as a negative token count
    // via an unchecked ulong→long cast.
    [Test]
    public async Task Overlarge_token_varint_does_not_produce_a_negative_count() {
        // field 2 (input) = a varint above long.MaxValue → treated as absent (0), never negative.
        var counts     = Concat(VarintRaw(2, ulong.MaxValue), Varint(3, 246));
        var usageParts = Concat(Ld(4, counts), Ld(19, "m"u8.ToArray()));
        var blob       = Ld(1, usageParts);

        var row = AntigravityGenMetadata.TryDecode(blob);
        // output (246) is still valid so the row decodes; input is 0 (rejected), not negative.
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Value.InputTokens).IsGreaterThanOrEqualTo(0L);
        await Assert.That(row.Value.OutputTokens).IsEqualTo(246L);
    }

    static byte[] VarintRaw(int field, ulong v) {
        var b = new List<byte>();
        WriteVarint(b, (ulong)(field << 3)); // wiretype 0
        WriteVarint(b, v);
        return b.ToArray();
    }

    [Test]
    public async Task Usage_line_matches_the_server_contract_shape() {
        var row  = new AntigravityUsageRow("gemini-default", 19360, 246, 16275);
        var line = AntigravityGenMetadata.ToUsageLine(row, genRow: 3);

        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        await Assert.That(r.GetProperty("type").GetString()).IsEqualTo("USAGE");
        await Assert.That(r.GetProperty("gen_row").GetInt64()).IsEqualTo(3L);
        await Assert.That(r.GetProperty("model").GetString()).IsEqualTo("gemini-default");
        await Assert.That(r.GetProperty("input_tokens").GetInt64()).IsEqualTo(19360L);
        await Assert.That(r.GetProperty("output_tokens").GetInt64()).IsEqualTo(246L);
        await Assert.That(r.GetProperty("cache_read_tokens").GetInt64()).IsEqualTo(16275L);
        await Assert.That(r.GetProperty("cache_write_tokens").GetInt64()).IsEqualTo(0L);
    }

    [Test]
    public async Task Usage_line_includes_created_at_anchor_when_provided() {
        var row  = new AntigravityUsageRow("gemini-default", 100, 10, 0);
        var line = AntigravityGenMetadata.ToUsageLine(row, genRow: 1, createdAt: "2026-07-02T19:04:57Z");
        using var doc = JsonDocument.Parse(line);
        await Assert.That(doc.RootElement.GetProperty("created_at").GetString()).IsEqualTo("2026-07-02T19:04:57Z");

        // Omitted when no anchor is available.
        var bare = AntigravityGenMetadata.ToUsageLine(row, genRow: 1);
        await Assert.That(JsonDocument.Parse(bare).RootElement.TryGetProperty("created_at", out _)).IsFalse();
    }
}
