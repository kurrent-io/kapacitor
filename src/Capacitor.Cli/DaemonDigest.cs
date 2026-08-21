using System.Security.Cryptography;

namespace Capacitor.Cli;

/// <summary>Build-time embedded SHA-256 of the daemon binary shipped alongside this CLI build.
/// <see cref="Expected"/> is a compile-time constant baked in by the <c>GenerateDaemonDigest</c>
/// MSBuild target (see Capacitor.Cli.csproj): the release pipeline passes
/// <c>-p:KcapDaemonDigest=&lt;hex&gt;</c> after hashing the just-published daemon binary; a dev/CI
/// build without that property carries the 64-zeros <see cref="Placeholder"/> instead. Everything
/// here fails CLOSED — a placeholder or unreadable file never reports a match.</summary>
public static partial class DaemonDigest {
    public static string Expected => GeneratedDigest.Value;

    public const string Placeholder = "0000000000000000000000000000000000000000000000000000000000000000";

    public static bool IsUsable => Expected != Placeholder && Expected.Length == 64;

    public static bool Matches(string filePath) {
        if (!IsUsable) return false; // fail closed
        try {
            return HashOf(filePath) == Expected;
        } catch {
            return false; // unreadable evidence → fail closed
        }
    }

    /// <summary>Seam for tests: computes the lowercase hex SHA-256 of a file's content the same
    /// way <see cref="Matches"/> does, independent of whether a real digest is embedded.</summary>
    internal static string HashOf(string filePath) {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
