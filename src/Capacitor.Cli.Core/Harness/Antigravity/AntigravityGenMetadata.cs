using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Antigravity;

/// <summary>Per-<c>gen_metadata</c>-row token usage decoded from the protobuf blob.</summary>
public readonly record struct AntigravityUsageRow(
    string? Model,
    long    InputTokens,
    long    OutputTokens,
    long    CacheReadTokens
);

/// <summary>
/// Decoder for Antigravity's <c>gen_metadata.data</c> protobuf blob. Antigravity
/// keeps per-generation tokens/model in the sibling SQLite db, not the JSONL transcript, and
/// ships NO <c>.proto</c>, so field numbers are pinned EMPIRICALLY against real Antigravity
/// 2.2.1 blobs (verified across many rows / conversations):
/// <list type="bullet">
///   <item>top-level field <c>1</c> = the usage message;</item>
///   <item>within it, field <c>4</c> = token counts: <c>.2</c> input(prompt), <c>.3</c>
///     output(candidates), <c>.5</c> cached-input (absent ⇒ 0, i.e. nothing cached);</item>
///   <item>field <c>19</c> = model id string (e.g. <c>gemini-default</c>), field <c>21</c> =
///     human label (e.g. <c>Gemini 3.5 Flash (Medium)</c>) — used as a fallback.</item>
/// </list>
/// Everything is best-effort / fail-open: a malformed or schema-drifted blob yields
/// <c>null</c> (no cost recorded) rather than throwing. Cache-WRITE and reasoning/thinking
/// tokens are not emitted — Antigravity's implicit caching reports no write count, and the
/// reasoning bucket's field is not yet confidently identified (a follow-up); the server
/// folds reasoning into output for billing anyway, so omitting it under-counts rather than
/// mis-bills.
/// </summary>
public static class AntigravityGenMetadata {
    // ── protobuf field numbers (empirically pinned; see class remarks) ──
    const int FieldUsageMessage = 1;   // top-level
    const int FieldCounts       = 4;   // within the usage message
    const int FieldModelId      = 19;  // within the usage message
    const int FieldModelLabel   = 21;  // within the usage message
    const int CountInput        = 2;   // within counts
    const int CountOutput       = 3;   // within counts
    const int CountCacheRead    = 5;   // within counts

    public static AntigravityUsageRow? TryDecode(ReadOnlySpan<byte> blob) {
        try {
            if (!TryFindMessage(blob, FieldUsageMessage, out var usage)) return null;

            string? model = null;
            if (TryFindMessage(usage, FieldModelId, out var idBytes) && idBytes.Length > 0)
                model = Utf8(idBytes);
            if (model is null && TryFindMessage(usage, FieldModelLabel, out var labelBytes) && labelBytes.Length > 0)
                model = Utf8(labelBytes);

            if (!TryFindMessage(usage, FieldCounts, out var counts)) return null;

            var input     = FindVarint(counts, CountInput)     ?? 0;
            var output    = FindVarint(counts, CountOutput)    ?? 0;
            var cacheRead = FindVarint(counts, CountCacheRead) ?? 0;

            // A row with no tokens at all is not useful cost data.
            if (input == 0 && output == 0 && cacheRead == 0) return null;

            return new AntigravityUsageRow(model, input, output, cacheRead);
        } catch {
            return null;
        }
    }

    /// <summary>
    /// The synthetic <c>USAGE</c> transcript line the server's antigravity normalizer maps
    /// to one <c>AntigravityUsageBackfilledEvent</c>. <paramref name="genRow"/> is the
    /// <c>gen_metadata.idx</c>; the server derives the event id from (session, gen_row), so a
    /// re-emitted row dedupes while a new row adds. <paramref name="createdAt"/> anchors the
    /// event timestamp to the conversation's most-recent transcript step (the row is decoded
    /// right after the generation that produced that step), so the backfill's recency reflects
    /// the turn rather than the event-store write time (review, S4).
    /// </summary>
    public static string ToUsageLine(AntigravityUsageRow row, long genRow, string? createdAt = null) {
        var o = new JsonObject {
            ["type"]               = "USAGE",
            ["gen_row"]            = genRow,
            ["input_tokens"]       = row.InputTokens,
            ["output_tokens"]      = row.OutputTokens,
            ["cache_read_tokens"]  = row.CacheReadTokens,
            ["cache_write_tokens"] = 0,
            ["reasoning_tokens"]   = 0
        };
        if (row.Model is not null) o["model"] = row.Model;
        if (createdAt is not null) o["created_at"] = createdAt;
        return o.ToJsonString();
    }

    // ── minimal protobuf reader (wire types 0 varint / 2 length-delimited only) ──

    static bool TryFindMessage(ReadOnlySpan<byte> buf, int field, out ReadOnlySpan<byte> value) {
        var i = 0;
        while (i < buf.Length) {
            if (!TryReadTag(buf, ref i, out var fn, out var wt)) break;
            if (wt == 2) {
                if (!TryReadVarint(buf, ref i, out var len)) break;
                if (len > (ulong)(buf.Length - i)) break; // length past the buffer — malformed
                var take = (int)len;
                if (fn == field) { value = buf.Slice(i, take); return true; }
                i += take;
            } else if (!SkipScalar(buf, ref i, wt)) {
                break;
            }
        }
        value = default;
        return false;
    }

    static long? FindVarint(ReadOnlySpan<byte> buf, int field) {
        var i = 0;
        while (i < buf.Length) {
            if (!TryReadTag(buf, ref i, out var fn, out var wt)) break;
            if (wt == 0) {
                if (!TryReadVarint(buf, ref i, out var v)) break;
                // A token count above long.MaxValue is malformed — return null (absent) rather
                // than an unchecked ulong→long cast that would surface as a negative count.
                if (fn == field) return v > long.MaxValue ? null : (long)v;
            } else if (wt == 2) {
                if (!TryReadVarint(buf, ref i, out var len)) break;
                if (len > (ulong)(buf.Length - i)) break; // length past the buffer — malformed
                i += (int)len;
            } else if (!SkipScalar(buf, ref i, wt)) {
                break;
            }
        }
        return null;
    }

    static bool TryReadTag(ReadOnlySpan<byte> buf, ref int i, out int fieldNumber, out int wireType) {
        fieldNumber = 0; wireType = 0;
        if (!TryReadVarint(buf, ref i, out var tag)) return false;
        fieldNumber = (int)(tag >> 3);
        wireType    = (int)(tag & 0x7);
        return true;
    }

    static bool SkipScalar(ReadOnlySpan<byte> buf, ref int i, int wireType) {
        switch (wireType) {
            case 0: return TryReadVarint(buf, ref i, out _);   // varint
            case 1: i += 8; return i <= buf.Length;            // 64-bit
            case 5: i += 4; return i <= buf.Length;            // 32-bit
            default: return false;                             // groups / unknown — bail
        }
    }

    static bool TryReadVarint(ReadOnlySpan<byte> buf, ref int i, out ulong value) {
        value = 0;
        var shift = 0;
        while (i < buf.Length) {
            var b = buf[i++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift >= 64) return false; // overlong varint — malformed
        }
        return false; // truncated
    }

    static string Utf8(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b);
}
