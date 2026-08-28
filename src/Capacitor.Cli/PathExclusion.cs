using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// Path-based session exclusion. Matches a session's <c>cwd</c> against a
/// configured list of directories, treating descendants as excluded too.
/// Resolves symlinks at the leaf so worktree symlinks stored in config still
/// match cwds reported as the canonical path (or vice versa).
/// </summary>
static class PathExclusion {
    public static bool IsExcluded(string? cwd, IReadOnlyList<string>? excludedPaths, UserHome home) {
        if (string.IsNullOrWhiteSpace(cwd)) return false;
        if (excludedPaths is null or { Count: 0 }) return false;

        string normalizedCwd;

        try {
            normalizedCwd = Normalize(cwd, home);
        } catch {
            return false;
        }

        if (string.IsNullOrEmpty(normalizedCwd)) return false;

        foreach (var entry in excludedPaths) {
            // Hand-edited configs can contain null/empty/whitespace entries; treat them
            // as no-ops rather than crashing hook forwarding via Path.GetRelativePath.
            if (string.IsNullOrWhiteSpace(entry)) continue;

            try {
                var normalizedEntry = Normalize(entry, home);

                if (string.IsNullOrEmpty(normalizedEntry)) continue;
                if (Contains(normalizedEntry, normalizedCwd)) return true;
            } catch {
                // best effort: a bad entry never blocks evaluation of the rest
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a user-supplied path to an absolute form with all symlinks
    /// expanded (including parent components), and no trailing separator.
    /// Expands <c>~</c> to the given home dir. Non-existent components are
    /// preserved as-is.
    /// </summary>
    public static string Normalize(string path, UserHome home) {
        if (string.IsNullOrEmpty(path)) return path;

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
                        || path.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            path = path.Length == 1 ? home.Path : Path.Combine(home.Path, path[2..]);
        }

        var full = Path.GetFullPath(path);
        var resolved = ResolveAllSymlinks(full);

        if (resolved.Length > 1) {
            resolved = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return resolved;
    }

    /// <summary>
    /// Walks an absolute path component-by-component, resolving every
    /// intermediate directory symlink. Stops resolving as soon as a component
    /// doesn't exist on disk; the unresolved tail is appended verbatim so paths
    /// that haven't been created yet still normalize consistently.
    /// </summary>
    const int MaxSymlinkResolveDepth = 32;

    static string ResolveAllSymlinks(string absolutePath, int depth = 0) {
        if (depth > MaxSymlinkResolveDepth) return absolutePath;

        var root = Path.GetPathRoot(absolutePath);

        if (string.IsNullOrEmpty(root)) return absolutePath;

        var remainder = absolutePath[root.Length..];
        var parts     = remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                                        StringSplitOptions.RemoveEmptyEntries);
        var current   = root;
        var i         = 0;

        for (; i < parts.Length; i++) {
            var next = Path.Combine(current, parts[i]);

            try {
                if (!Directory.Exists(next)) break;

                var info     = new DirectoryInfo(next);
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true);

                if (resolved is not null) {
                    // The symlink target may live under a different symlinked ancestor.
                    // Re-resolve from the root so every component of the target is canonical too.
                    current = ResolveAllSymlinks(resolved.FullName, depth + 1);
                } else {
                    current = next;
                }
            } catch {
                current = next;
                i++;

                break;
            }
        }

        // Append any unresolved tail (path components past a non-existent or errored segment).
        for (; i < parts.Length; i++) current = Path.Combine(current, parts[i]);

        return current;
    }

    static bool Contains(string parent, string candidate) {
        // Path.GetRelativePath uses OS-appropriate case sensitivity (case-insensitive
        // on Windows/macOS, case-sensitive on Linux), so the containment check
        // matches platform path semantics without manual fiddling.
        var rel = Path.GetRelativePath(parent, candidate);

        if (rel == ".") return true;
        if (Path.IsPathRooted(rel)) return false; // different volume/root

        // Reject only literal parent-directory references — `..` exactly or
        // `..` followed by a separator. A filename that merely starts with `..`
        // (e.g. `..scratch`, `..data/session`) is a legitimate descendant.
        if (rel == "..") return false;
        if (rel.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) return false;
        if (rel.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)) return false;

        return true;
    }
}
