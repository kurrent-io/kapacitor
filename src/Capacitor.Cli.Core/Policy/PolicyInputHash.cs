namespace Capacitor.Cli.Core.Policy;

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Key-order-insensitive, value-representation-insensitive digest of (tool_name, tool_input): the
/// same call re-presented at a later seam must hash identically even if the vendor reserializes
/// the object with different key order, string escaping, or numeric spelling.
/// </summary>
public static class PolicyInputHash {
    public static string Compute(string? toolName, JsonElement? toolInput) {
        using var ms = new MemoryStream();
        void Write(string s) {
            var bytes = Encoding.UTF8.GetBytes(s);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)bytes.Length);
            ms.Write(len);
            ms.Write(bytes);
        }
        Write(toolName ?? "");
        Write(toolInput is { } el ? Canonical(el) : "");
        return Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
    }

    static string Canonical(JsonElement el) {
        var sb = new StringBuilder();
        Append(sb, el);
        return sb.ToString();
    }

    static void Append(StringBuilder sb, JsonElement el) {
        if (el.IsObject) {
            sb.Append('{');
            var first = true;
            foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) {
                if (!first) sb.Append(',');
                first = false;
                AppendQuoted(sb, p.Name);
                sb.Append(':');
                Append(sb, p.Value);
            }
            sb.Append('}');
        } else if (el.IsArray) {
            sb.Append('[');
            var firstItem = true;
            foreach (var item in el.EnumerateArray()) {
                if (!firstItem) sb.Append(',');
                firstItem = false;
                Append(sb, item);
            }
            sb.Append(']');
        } else if (el.IsString) {
            AppendQuoted(sb, el.GetString() ?? "");
        } else if (el.IsNumber) {
            // A JSON number's raw text isn't canonical ("1" vs "1.0"): compare by value
            // instead, falling back to the raw text only when it's outside decimal's range.
            sb.Append(el.TryGetDecimal(out var d) ? CanonicalNumber(d) : el.GetRawText());
        } else { // True, False, Null
            sb.Append(el.GetRawText());
        }
    }

    static void AppendQuoted(StringBuilder sb, string value) =>
        sb.Append('"').Append(Encoding.UTF8.GetString(JsonEncodedText.Encode(value).EncodedUtf8Bytes)).Append('"');

    // decimal.ToString preserves the source's trailing zeros (1.0m -> "1.0"), which would hash
    // "1" and "1.0" differently — trim them so value, not spelling, decides equality.
    static string CanonicalNumber(decimal d) {
        var s = d.ToString(CultureInfo.InvariantCulture);
        if (!s.Contains('.')) return s;
        s = s.TrimEnd('0');
        return s.EndsWith('.') ? s[..^1] : s;
    }
}
