using System.Security.Cryptography;

namespace Capacitor.Cli.Core;

/// <summary>
/// D0. Pure prefix-hash comparison used by both the phase-0 empirical verification harness
/// (<c>kcap cursor-verify-appendonly</c>) and the runtime two-zone rewrite guard
/// (<c>Capacitor.Cli.Commands.CursorRewriteGuard</c>). Append-only ⇔ length is monotonically
/// non-decreasing AND re-hashing the first L_earlier bytes at the later time reproduces the
/// earlier hash.
/// </summary>
public static class CursorAppendOnlyProbe {
    public readonly record struct Sample(long Length, string PrefixSha256);

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static bool PrefixStable(Sample earlier, Sample later, ReadOnlySpan<byte> laterBytes) {
        if (later.Length < earlier.Length) return false;
        if (earlier.Length > laterBytes.Length) return false;
        var reHash = Sha256Hex(laterBytes[..(int)earlier.Length]);
        return string.Equals(reHash, earlier.PrefixSha256, StringComparison.Ordinal);
    }
}
