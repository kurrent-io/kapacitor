using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Harness.Cursor;

/// <summary>CLI side of the pinned Cursor prompt canonicalization contract.
/// Body MUST stay byte-identical to the server's Capacitor.Cursor.CursorPromptCanonicalizer.</summary>
public static class CursorPromptCanonicalizer {
    const string TsOpen = "<timestamp>", TsClose = "</timestamp>";
    const string QOpen  = "<user_query>", QClose = "</user_query>";

    public static string Canonicalize(string? text) {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var rest = text.AsSpan().Trim();
        if (rest.StartsWith(TsOpen)) {
            var close = rest.IndexOf(TsClose);
            if (close < 0) return rest.ToString();
            rest = rest[(close + TsClose.Length)..];
            if (rest.StartsWith("\r\n")) rest = rest[2..];
            else if (rest.StartsWith("\n")) rest = rest[1..];
            else return text.AsSpan().Trim().ToString();
            rest = rest.Trim();
        }
        if (!rest.StartsWith(QOpen) || !rest.EndsWith(QClose)) return rest.ToString();
        var inner = rest[QOpen.Length..^QClose.Length];
        if (inner.StartsWith("\r\n")) inner = inner[2..];
        else if (inner.StartsWith("\n")) inner = inner[1..];
        if (inner.EndsWith("\r\n")) inner = inner[..^2];
        else if (inner.EndsWith("\n")) inner = inner[..^1];
        return inner.ToString();
    }

    public static string Hash(string? text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(text))));
}
