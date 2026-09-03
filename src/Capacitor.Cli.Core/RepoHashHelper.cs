using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

public static partial class RepoHashHelper {
    public static string ComputeRepoHash(string owner, string repoName) {
        var input = $"{owner}/{repoName}".ToLowerInvariant();
        var hash  = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>Accepts the two shapes a caller may name a repo by: <c>owner/name</c>, hashed here,
    /// or a 16-character lowercase hex hash, passed through. Uppercase hex is rejected rather than
    /// folded so a hash copied from the wrong place fails loudly.</summary>
    public static bool TryParseRepoRef(string value, out string repoHash) {
        repoHash = "";

        if (string.IsNullOrWhiteSpace(value)) return false;

        if (Hash().IsMatch(value)) {
            repoHash = value;

            return true;
        }

        var parts = value.Split('/');

        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;
        if (parts[0].Any(char.IsWhiteSpace) || parts[1].Any(char.IsWhiteSpace)) return false;

        repoHash = ComputeRepoHash(parts[0], parts[1]);

        return true;
    }

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex Hash();
}
