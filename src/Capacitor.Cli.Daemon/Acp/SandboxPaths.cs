namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Path resolution shared by everything that writes a sandbox profile. Separate from
/// <see cref="BorrowedReviewSandbox"/> because <see cref="BorrowedReviewRuntimeRoots"/> needs the same
/// resolution to find the real program behind a launcher symlink, and two copies of this logic would
/// be two chances to get the ancestor walk wrong.
/// </summary>
internal static class SandboxPaths {
    /// <summary>How many symlinked ancestors to follow before giving up (loop guard).</summary>
    const int MaxLinkHops = 64;

    /// <summary>The physical path, with symlinked ANCESTORS resolved — not just a symlinked leaf.
    ///
    /// <para>Resolving only the leaf is not enough and the difference is not academic: on macOS both
    /// <c>/tmp</c> and <c>/var</c> are symlinks, so a path under either has a real path its leaf is
    /// not a link to. The sandbox matches on the resolved path, so granting only what the caller
    /// passed produces a reviewer that cannot read its own snapshot — the original blind-review defect
    /// wearing a different hat. Caught by the enforcement test, which is why that test runs a real
    /// process instead of asserting on the profile string.</para></summary>
    internal static string? TryResolvePhysical(string path) {
        try {
            var current = Path.GetFullPath(path);

            // Walk root-first so an ancestor's target is applied before the components below it.
            for (var hop = 0; hop < MaxLinkHops; hop++) {
                var link = FirstLinkedAncestor(current);

                if (link is null) return current;

                var target = ResolveLinkTarget(link);

                if (target is null) return current;

                // Re-root the remainder of the path under the ancestor's target.
                var remainder = current[link.Length..].TrimStart(Path.DirectorySeparatorChar);
                current = remainder.Length == 0 ? target : Path.Combine(target, remainder);
            }

            return current;
        } catch (Exception) {
            // A path that cannot be inspected is not a reason to widen a profile — callers still
            // grant the unresolved form, and a genuinely wrong path fails loudly at spawn instead.
            return null;
        }
    }

    /// <summary>Both the caller's form and its resolved form, deduplicated.
    ///
    /// <para>Both are granted deliberately. Granting only what the caller passed fails when an
    /// ancestor is a symlink (the sandbox matches the resolved path); granting only the resolved form
    /// fails when the path does not exist yet at profile-build time and resolution returns null.</para></summary>
    internal static IReadOnlyList<string> BothForms(string path) {
        var resolved = TryResolvePhysical(path);

        return resolved is null || string.Equals(resolved, path, StringComparison.Ordinal)
            ? [path]
            : [path, resolved];
    }

    /// <summary>Whether <paramref name="path"/> IS a filesystem root (<c>/</c>, or a Windows drive
    /// root) rather than a directory within one.
    ///
    /// <para>Load-bearing rather than defensive tidiness, which is why it lives here and is used by
    /// both the grant resolver and the profile writer. A vendor binary resolving to a path directly
    /// under the root — <c>/copilot</c> — makes its containing directory <c>/</c>, and granting
    /// <c>(subpath "/")</c> hands the reviewer the entire filesystem: the profile still parses, the
    /// vendor still starts, and every containment test that probes a NAMED tree still passes while the
    /// boundary is gone.</para></summary>
    internal static bool IsFilesystemRoot(string path) {
        try {
            var full = Path.GetFullPath(path);

            return string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal);
        } catch (Exception) {
            // An uninspectable path is treated AS a root: the callers' response is to refuse or drop
            // it, which is the safe direction for something about to become a filesystem grant.
            return true;
        }
    }

    /// <summary>A link target resolved through <see cref="FileSystemInfo.ResolveLinkTarget"/>, trying
    /// the directory and file shapes — a launcher on PATH is a symlink to a FILE, and
    /// <see cref="DirectoryInfo"/> does not resolve those.</summary>
    static string? ResolveLinkTarget(string path) {
        try {
            if (new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName is { } directoryTarget)
                return directoryTarget;
        } catch (Exception) {
            // Fall through to the file shape.
        }

        try {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>The shallowest ancestor of <paramref name="path"/> (inclusive) that is itself a
    /// symlink, or null when none is.</summary>
    static string? FirstLinkedAncestor(string path) {
        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var probe = "";

        foreach (var part in parts) {
            probe += Path.DirectorySeparatorChar + part;

            if (new DirectoryInfo(probe).LinkTarget is not null) return probe;
        }

        return null;
    }
}
