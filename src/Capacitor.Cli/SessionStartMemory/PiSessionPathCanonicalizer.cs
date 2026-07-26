using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.SessionStartMemory;

internal static class PiSessionPathCanonicalizer {
    public static bool TryHash(string? input, out string? hash) {
        hash = null;
        if (string.IsNullOrEmpty(input) || input.IndexOf('\0') >= 0 || !Path.IsPathRooted(input)) return false;
        try {
            var full = Path.GetFullPath(input);
            var root = Path.GetPathRoot(full);
            if (!string.Equals(full, root, StringComparison.Ordinal))
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            full = full.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            full = full.Normalize(NormalizationForm.FormC);
            if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
            hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)));
            return true;
        } catch { return false; }
    }
}
