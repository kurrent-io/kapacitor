namespace Capacitor.Cli.Core.Policy;

public static class GlobPattern {
    public static bool IsMatch(string pattern, string text) {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length) {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
