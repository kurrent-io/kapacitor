using System.Text;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// An OS-enforced filesystem boundary around a borrowed reviewer process, built as an inline
/// <c>sandbox-exec</c> profile: deny by default, then grant two writable trees (the snapshot under
/// review and a per-launch vendor state directory) plus the read-only paths the vendor needs to start.
///
/// <para>The boundary is here, below the vendor, rather than in the tool allowlist because the
/// allowlist bounds writes but not reads: the vendor's answer to an out-of-bounds path is a permission
/// request, which an unattended daemon answers, so read containment would hold only while the build
/// keeps asking. Nothing under the user's home is granted — <c>HOME</c>/<c>TMPDIR</c> are redirected
/// (see <see cref="HomeDirectoryIn"/>), authentication is brokered
/// (<see cref="BorrowedReviewAuthBroker"/>), and runtime paths are narrowed
/// (<see cref="BorrowedReviewRuntimeRoots"/>) so the vendor can start without them.</para>
///
/// <para>Design, probe results and the live evidence:
/// <c>docs/superpowers/specs/2026-07-29-ai1584-borrowed-reviewer-sandbox-grants-design.md</c> in
/// kcap-server.</para>
/// </summary>
internal static class BorrowedReviewSandbox {
    internal const string SandboxExecPath = "/usr/bin/sandbox-exec";

    /// <summary>Whether this host can enforce the boundary at all. Resolved once, at policy
    /// resolution rather than per spawn, and a false here means the platform entry is unsupported —
    /// there is no launch that proceeds without the sandbox.</summary>
    internal static bool Available { get; } = File.Exists(SandboxExecPath);

    /// <summary>Read-only system locations, none of which hold per-user data. <c>/Library</c> is
    /// deliberately absent: probed as unnecessary, and <c>/Library/Application Support</c> alone makes
    /// it a per-application data tree.</summary>
    static IEnumerable<string> SystemReadPaths() {
        yield return "/usr";
        yield return "/bin";
        yield return "/sbin";
        yield return "/System";
        yield return "/private/var/select";
    }

    /// <summary>Builds the inline profile for one borrowed launch.</summary>
    /// <param name="snapshotPath">The daemon-owned snapshot the reviewer may read and write.</param>
    /// <param name="stateRootPath">The per-launch vendor state directory backing <c>HOME</c> and
    /// <c>TMPDIR</c> — writable, and outside the snapshot so a per-round refresh neither wipes the
    /// running vendor's state nor presents that state to the reviewer as content under review.</param>
    /// <param name="runtimeReadPaths">Read-only roots the vendor needs to start, from
    /// <see cref="BorrowedReviewRuntimeRoots.Resolve"/>.</param>
    internal static string BuildProfile(
            string snapshotPath, string stateRootPath, IReadOnlyList<string> runtimeReadPaths) {
        // A filesystem root here emits (subpath "/") and hands over the whole machine while the profile
        // still parses and every named-tree containment test stays green. The daemon-chosen paths throw
        // (a root there is an upstream bug worth surfacing); derived runtime roots are dropped, which
        // fails loudly at exec instead.
        RejectFilesystemRoot(snapshotPath, nameof(snapshotPath));
        RejectFilesystemRoot(stateRootPath, nameof(stateRootPath));

        var snapshots = SandboxPaths.BothForms(snapshotPath);
        var state     = SandboxPaths.BothForms(stateRootPath);
        runtimeReadPaths = [.. runtimeReadPaths.Where(p => !SandboxPaths.IsFilesystemRoot(p))];

        var sb = new StringBuilder();
        sb.Append("(version 1)(deny default)(import \"system.sb\")");
        sb.Append("(allow process-fork process-exec)");
        // Outbound only: the reviewer calls the vendor's API and has no reason to listen. Unqualified
        // network* additionally permitted inbound and bind.
        sb.Append("(allow network-outbound)");
        // Metadata-only access is deliberately left open: the runtime stats paths it never reads, and
        // a stat leaks existence rather than contents.
        sb.Append("(allow file-read-metadata)");

        sb.Append("(allow file-read*");
        foreach (var p in snapshots)          sb.Append(Subpath(p));
        foreach (var p in state)              sb.Append(Subpath(p));
        foreach (var p in SystemReadPaths())  sb.Append(Subpath(p));
        foreach (var p in runtimeReadPaths)   sb.Append(Subpath(p));
        sb.Append(')');

        sb.Append("(allow file-write*");
        foreach (var p in snapshots) sb.Append(Subpath(p));
        foreach (var p in state)     sb.Append(Subpath(p));
        sb.Append(')');

        return sb.ToString();
    }

    /// <summary>The argv that runs <paramref name="binaryPath"/> under the profile.</summary>
    internal static IReadOnlyList<string> WrapArgv(
            string profile, string binaryPath, IEnumerable<string> argv) =>
        ["-p", profile, binaryPath, .. argv];

    /// <summary>The two subdirectories of the per-launch state root, handed to the vendor as
    /// <c>HOME</c> and <c>TMPDIR</c>. Split so the vendor's profile and its scratch files are
    /// distinguishable when a launch is being diagnosed.</summary>
    internal static string HomeDirectoryIn(string stateRoot) => Path.Combine(stateRoot, "home");
    internal static string TempDirectoryIn(string stateRoot) => Path.Combine(stateRoot, "tmp");

    /// <summary>Materializes the per-launch state directories. Called at the spawn seam rather than
    /// from the pure argv builder, which stays free of side effects.</summary>
    internal static void CreateStateDirectories(string stateRoot) {
        Directory.CreateDirectory(HomeDirectoryIn(stateRoot));
        Directory.CreateDirectory(TempDirectoryIn(stateRoot));
    }

    static void RejectFilesystemRoot(string path, string parameterName) {
        if (SandboxPaths.IsFilesystemRoot(path))
            throw new ArgumentException(
                $"A borrowed-review sandbox cannot be drawn at a filesystem root ('{path}') — that grant " +
                "is the entire machine.", parameterName);
    }

    // Profile strings are SCM-quoted. A path containing a quote or backslash would otherwise break out
    // of its own (subpath "...") form and could re-grant the filesystem, so escape rather than trust
    // that daemon-owned paths are always tame.
    static string Subpath(string path) => $"(subpath \"{Escape(path)}\")";
    static string Escape(string path)  => path.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
