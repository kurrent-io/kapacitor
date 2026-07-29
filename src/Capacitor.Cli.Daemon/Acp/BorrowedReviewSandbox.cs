using System.Text;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// An OS-enforced filesystem boundary around a borrowed reviewer process, built as an inline
/// <c>sandbox-exec</c> profile.
///
/// <para><b>Why this exists rather than trusting the tool allowlist.</b> The exclusive
/// <c>--available-tools</c> allowlist makes write and exec unrepresentable, which is a real write
/// boundary — but it is not a READ boundary. Widening the allowlist to the read tools also widens
/// what a path-taking read tool can be pointed at, and the vendor's own answer to an out-of-bounds
/// path is a permission request that an unattended daemon is in the business of answering. That
/// makes read containment a property of the vendor build: it holds while the build keeps asking, and
/// silently disappears in a build that stops. Confidentiality cannot rest on that — the reviewer runs
/// unattended on prompt-injectable content and keeps an explicit result channel off the machine.</para>
///
/// <para>So the boundary is moved below the vendor entirely. The profile denies filesystem access by
/// default and re-grants the snapshot plus the minimum needed to start the vendor: system and runtime
/// paths, the vendor's own config/cache, and the keychain it authenticates against. Verified live —
/// under this profile a reviewer reads the snapshot normally, and an outside read fails <b>even when
/// the permission request for it is explicitly granted</b>. That last clause is the whole point: it is
/// what makes the boundary independent of what the vendor decides to ask.</para>
///
/// <para>This is the same containment class Codex already advertises as <c>native-tool-clamp</c> —
/// an OS sandbox with the read tools intact — arrived at from the other direction.</para>
/// </summary>
internal static class BorrowedReviewSandbox {
    internal const string SandboxExecPath = "/usr/bin/sandbox-exec";

    /// <summary>Whether this host can enforce the boundary at all. Resolved once, at policy
    /// resolution rather than per spawn, and a false here means the platform entry is unsupported —
    /// there is no launch that proceeds without the sandbox.</summary>
    internal static bool Available { get; } = File.Exists(SandboxExecPath);

    /// <summary>Read-only paths the vendor needs to start that are NOT the snapshot. Deliberately
    /// coarse for system locations and narrow for user ones: a broad <c>$HOME</c> grant would readmit
    /// exactly the exfiltration this profile exists to stop, so only the vendor's own directories are
    /// listed, plus <c>$HOME</c> itself as a literal (not a subpath) because the runtime stats it.</summary>
    static IEnumerable<string> SystemReadPaths() {
        yield return "/usr";
        yield return "/bin";
        yield return "/sbin";
        yield return "/System";
        yield return "/Library";
        yield return "/opt/homebrew";
        yield return "/private/var/select";
    }

    /// <summary>Builds the inline profile for one borrowed launch.</summary>
    /// <param name="snapshotPath">The daemon-owned snapshot the reviewer may read and write.</param>
    /// <param name="home">The user's home directory, used to locate the vendor's own state.</param>
    internal static string BuildProfile(string snapshotPath, string home) {
        // Both the given path and its symlink-resolved form are granted. On macOS /tmp is a symlink
        // to /private/tmp, and the sandbox matches on the RESOLVED path — granting only what the
        // caller passed produces a reviewer that cannot read its own snapshot, which is the original
        // bug wearing a different hat. Granting only the resolved form fails the other way when the
        // path does not exist yet at build time and resolution returns null.
        var snapshots = new List<string> { snapshotPath };
        var resolved  = TryResolvePhysical(snapshotPath);

        if (resolved is not null && !string.Equals(resolved, snapshotPath, StringComparison.Ordinal))
            snapshots.Add(resolved);

        string[] vendorState = [
            Path.Combine(home, ".copilot"),
            Path.Combine(home, "Library", "Caches", "copilot"),
            // Authentication lives in the keychain; without it session/new answers
            // "Authentication required" and the reviewer never starts.
            Path.Combine(home, "Library", "Keychains")
        ];

        var sb = new StringBuilder();
        sb.Append("(version 1)(deny default)(import \"system.sb\")");
        sb.Append("(allow process-fork process-exec)(allow network*)(allow mach-lookup)");
        // Metadata-only access is deliberately left open: the runtime stats paths it never reads, and
        // a stat leaks existence rather than contents.
        sb.Append("(allow file-read-metadata)");

        sb.Append("(allow file-read*");
        foreach (var p in snapshots)          sb.Append(Subpath(p));
        foreach (var p in SystemReadPaths())  sb.Append(Subpath(p));
        foreach (var p in vendorState)        sb.Append(Subpath(p));
        sb.Append(Literal(home));
        sb.Append(')');

        sb.Append("(allow file-write*");
        foreach (var p in snapshots)   sb.Append(Subpath(p));
        foreach (var p in vendorState) sb.Append(Subpath(p));
        sb.Append(Subpath("/private/var/folders"));
        sb.Append(Subpath("/dev"));
        sb.Append(')');

        return sb.ToString();
    }

    /// <summary>The argv that runs <paramref name="binaryPath"/> under the profile.</summary>
    internal static IReadOnlyList<string> WrapArgv(
            string profile, string binaryPath, IEnumerable<string> argv) =>
        ["-p", profile, binaryPath, .. argv];

    /// <summary>The physical path, with symlinked ANCESTORS resolved — not just a symlinked leaf.
    ///
    /// <para>Resolving only the leaf is not enough and the difference is not academic: on macOS both
    /// <c>/tmp</c> and <c>/var</c> are symlinks, so a snapshot under either has a real path the leaf
    /// is not a link to. The sandbox matches on the resolved path, so granting only what the caller
    /// passed produces a reviewer that cannot read its own snapshot — the original bug, wearing a
    /// different hat. Caught by the enforcement test, which is why that test runs a real process
    /// instead of asserting on the profile string.</para></summary>
    static string? TryResolvePhysical(string path) {
        try {
            var current = Path.GetFullPath(path);

            // Walk root-first so an ancestor's target is applied before the components below it.
            for (var depth = 0; depth < 64; depth++) {
                var link = FirstLinkedAncestor(current);

                if (link is null) return current;

                var target = new DirectoryInfo(link).ResolveLinkTarget(returnFinalTarget: true)?.FullName;

                if (target is null) return current;

                // Re-root the remainder of the path under the ancestor's target.
                var remainder = current[link.Length..].TrimStart(Path.DirectorySeparatorChar);
                current = remainder.Length == 0 ? target : Path.Combine(target, remainder);
            }

            return current;
        } catch (Exception) {
            // A path that cannot be inspected is not a reason to widen the profile — the unresolved
            // form is still granted, and a genuinely wrong path fails loudly at spawn instead.
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

    // Profile strings are SCM-quoted. A path containing a quote or backslash would otherwise break out
    // of its own (subpath "...") form and could re-grant the filesystem, so escape rather than trust
    // that daemon-owned paths are always tame.
    static string Subpath(string path) => $"(subpath \"{Escape(path)}\")";
    static string Literal(string path) => $"(literal \"{Escape(path)}\")";
    static string Escape(string path)  => path.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
