using System.Runtime.InteropServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli;

/// <summary>
/// Applies user-configured path-prefix remaps to a transcript cwd so historic
/// sessions that recorded a since-renamed local repo path (e.g.
/// <c>~/dev/kapacitor-cli</c> after rename to <c>~/dev/kcap-cli</c>) can still
/// be matched to an on-disk git repository during <c>kcap import</c>.
///
/// Matching rules:
/// - <c>~</c> or <c>~/</c> (or <c>~\</c> on Windows) at the start of
///   <c>from</c>/<c>to</c> is expanded to the given home directory
///   (transcript cwds are absolute).
/// - Path-boundary prefix: <c>cwd == from</c> or <c>cwd starts with from + sep</c>
///   where <c>sep</c> is either <c>/</c> or <c>\</c>; so <c>/dev/kapacitor</c>
///   does NOT spuriously match <c>/dev/kapacitor-cli</c>.
/// - Comparisons are case-insensitive on Windows, case-sensitive elsewhere —
///   matching the host filesystem's behavior.
/// - Longest <c>from</c> wins when multiple rules could apply.
/// - At most one remap is applied (no chaining), to keep behavior predictable.
/// </summary>
static class CwdRemapper {
    static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string Apply(string cwd, IReadOnlyList<CwdRemap>? rules, UserHome home) =>
        Apply(cwd, rules, home.Path, PathComparison);

    // Internal seam for tests that need to pin the comparison policy (so the
    // case-sensitivity behavior can be exercised regardless of host OS).
    internal static string Apply(string cwd, IReadOnlyList<CwdRemap>? rules, string home, StringComparison comparison) {
        if (rules is null or { Count: 0 } || string.IsNullOrEmpty(cwd)) return cwd;

        CwdRemap? best     = null;
        string?   bestFrom = null;
        string?   bestTo   = null;
        var       bestLen  = -1;

        foreach (var rule in rules) {
            if (string.IsNullOrEmpty(rule.From) || rule.To is null) continue;

            var from = ExpandHome(rule.From, home);

            if (!IsPrefixMatch(cwd, from, comparison)) continue;

            if (from.Length > bestLen) {
                best     = rule;
                bestFrom = from;
                bestTo   = ExpandHome(rule.To, home);
                bestLen  = from.Length;
            }
        }

        if (best is null) return cwd;

        // cwd == from → use 'to' verbatim; otherwise replace prefix + keep the
        // tail (the leading separator of the tail was at position from.Length).
        return cwd.Length == bestFrom!.Length
            ? bestTo!
            : bestTo + cwd[bestFrom.Length..];
    }

    static string ExpandHome(string path, string home) {
        if (path.Length == 0 || path[0] != '~') return path;
        if (path.Length == 1) return home;                  // "~"
        if (IsSeparator(path[1])) return home + path[1..];  // "~/foo" or "~\foo"
        return path;                                        // "~user" / "~foo" — leave alone
    }

    static bool IsPrefixMatch(string cwd, string from, StringComparison comparison) {
        if (!cwd.StartsWith(from, comparison)) return false;
        if (cwd.Length == from.Length) return true;
        return IsSeparator(cwd[from.Length]);
    }

    internal static bool IsSeparator(char c) => c == '/' || c == '\\';
}
