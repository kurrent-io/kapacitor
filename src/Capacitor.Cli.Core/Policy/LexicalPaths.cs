namespace Capacitor.Cli.Core.Policy;

/// <summary>
/// Lexically resolves a path against a cwd: joins if relative, collapses <c>.</c>/<c>..</c>,
/// forward slashes. No filesystem access and no symlink resolution — a symlink escape is the
/// vendor sandbox's concern, not the policy engine's.
/// </summary>
public static class LexicalPaths {
    public static string? TryResolve(string? cwd, string? path) {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string joined;
        if (Path.IsPathRooted(path)) joined = path;
        else if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathRooted(cwd)) return null;
        else joined = cwd + "/" + path;
        var parts = joined.Replace('\\', '/').Split('/');
        var stack = new List<string>();
        var root = parts[0].Length == 0 ? "" : parts[0]; // "" for unix-absolute, "C:" for a drive
        foreach (var part in parts.Skip(1)) {
            if (part is "" or ".") continue;
            if (part == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else stack.Add(part);
        }
        return root + "/" + string.Join('/', stack);
    }
}
