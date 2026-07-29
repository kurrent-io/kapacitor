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
/// default and re-grants exactly two writable trees — the snapshot under review and a per-launch
/// vendor state directory — plus the read-only system and runtime paths the vendor needs to start.</para>
///
/// <para><b>The profile no longer grants anything under the user's home.</b> An earlier revision had
/// to grant recursive reads of <c>~/.copilot</c>, <c>~/Library/Keychains</c>, <c>/Library</c> and the
/// whole of <c>/opt/homebrew</c> so the vendor could start and authenticate — all data-bearing, all
/// reachable with no ACP interaction frame, so the <c>Fail</c> interaction policy never fired and the
/// sandbox permitted the read. Three changes closed them, and each is load-bearing:</para>
/// <list type="number">
/// <item>a <b>per-launch state directory</b> supplies <c>HOME</c> and <c>TMPDIR</c>, so the reviewer
/// gets an empty vendor profile instead of the user's prior sessions, command history and caches —
/// and, incidentally, no longer needs write access to <c>/private/var/folders</c> or <c>/dev</c>;</item>
/// <item><b>brokered authentication</b> (<see cref="BorrowedReviewAuthBroker"/>) replaces the keychain
/// grant, which the previous profile granted for WRITE as well as read;</item>
/// <item><b>runtime roots derived from the vendor binary</b>
/// (<see cref="BorrowedReviewRuntimeRoots"/>) replace the whole-prefix grants, admitting software
/// subdirectories while leaving configuration and service data unreadable.</item>
/// </list>
///
/// <para><b>Every remaining grant was probed, not assumed.</b> Removing the <c>system.sb</c> import
/// aborts the process before it emits a frame, so it stays. Unqualified <c>mach-lookup</c> and
/// <c>network*</c> were both narrowed after live runs completed an authenticated <c>session/new</c>
/// without them: the keychain was the only thing that ever needed the former.</para>
///
/// <para>Verified live end to end — under this profile a reviewer reads a snapshot's branch-only,
/// tracked-modified and untracked content normally, while reads of the keychain, the user's vendor
/// state, <c>/Library</c> and the prefix's config/data trees all fail <b>even when the permission
/// request for them is explicitly granted</b>. That last clause is the whole point: it is what makes
/// the boundary independent of what the vendor decides to ask.</para>
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

    /// <summary>Read-only system locations, none of which hold per-user data.
    ///
    /// <para><c>/Library</c> is deliberately absent — it was in an earlier revision and is not needed
    /// (probed: the vendor starts and authenticates without it), while
    /// <c>/Library/Application Support</c> alone makes it a per-application data tree. Everything
    /// vendor- or runtime-specific arrives through <see cref="BorrowedReviewRuntimeRoots"/> instead.</para></summary>
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
        // Belt and braces at the place that actually writes the grants. A filesystem root reaching any
        // of these emits (subpath "/") and silently hands over the whole machine — a profile that still
        // parses, a vendor that still starts, and every named-tree containment test still green. The
        // two daemon-chosen paths THROW, because a root there means something upstream is badly wrong
        // and a quiet fallback would hide it; the filesystem-derived runtime roots are dropped, because
        // the launch then fails loudly at exec instead.
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
