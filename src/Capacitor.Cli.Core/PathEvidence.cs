namespace Capacitor.Cli.Core;

/// Structural path-evidence probe shared by not-found-classification callers.
public static class PathEvidence {
    /// True when a not-found read failure at <paramref name="path"/> is explained by a link at the
    /// exact path, or a file/link ancestor, rather than a chain of directories never created.
    public static bool PathBlockedByFileOrLink(string path) {
        if (IsLink(path)) return true;

        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current)) {
            if (File.Exists(current)) return true;
            if (Directory.Exists(current)) return false;
            if (IsLink(current)) return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    static bool IsLink(string path) {
        try { return File.ResolveLinkTarget(path, returnFinalTarget: false) is not null; }
        catch { return false; }
    }
}
