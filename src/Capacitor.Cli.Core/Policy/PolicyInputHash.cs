namespace Capacitor.Cli.Core.Policy;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Key-order-insensitive digest of (tool_name, tool_input): the same call re-presented at a
/// later seam must hash identically even if the vendor reserializes the object.
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
        switch (el.ValueKind) {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(Encoding.UTF8.GetString(JsonEncodedText.Encode(p.Name).EncodedUtf8Bytes)).Append('"').Append(':');
                    Append(sb, p.Value);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in el.EnumerateArray()) {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    Append(sb, item);
                }
                sb.Append(']');
                break;
            default:
                sb.Append(el.GetRawText());
                break;
        }
    }
}
