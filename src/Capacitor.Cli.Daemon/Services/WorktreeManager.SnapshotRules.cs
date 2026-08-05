using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

public partial class WorktreeManager {
    /// <summary>Whether an entry name is git control data, compared case-insensitively.
    ///
    /// <para><b>Deliberately asymmetric with the marker-based <c>.capacitor</c> exclusion.</b> A real
    /// <c>.git</c> must never land in the snapshot: the standalone path runs <c>git init</c> over the
    /// result, and a copied gitfile or repository directory makes that re-initialise a DIFFERENT
    /// repository and commit into it — a write escape, not merely a leak. So the fail-safe direction here
    /// is DROP. A <c>.Capacitor</c> directory is inert content, so its fail-safe direction is KEEP, and it
    /// gets the marker treatment instead. The marker technique is unavailable here because we do not own
    /// the source's <c>.git</c>.</para>
    ///
    /// <para><b>Accepted cost:</b> on a case-sensitive filesystem, inert content in a directory literally
    /// named <c>.GIT</c> is dropped. Safety over fidelity, and consistent with this class's own
    /// <see cref="NormalizeRelativePath"/>, which already compares <c>.git</c> with OrdinalIgnoreCase.</para>
    /// </summary>
    internal static bool IsGitEntryName(string name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a symlink's RAW target may be recreated verbatim inside the snapshot.
    ///
    /// <para><b>Why "never escapes" rather than "finally resolves inside".</b> Final-resolution
    /// containment is unsound under relocation, and reachable with a completely quiescent source. A
    /// source-root link <c>self -> ../&lt;source-dir-name&gt;/secret</c> resolves back inside the source,
    /// so a final-resolution rule admits it. But the snapshot lives at
    /// <c>&lt;source&gt;/.capacitor/worktrees/&lt;name&gt;</c>, so the SAME raw target evaluated there
    /// becomes <c>&lt;…&gt;/worktrees/&lt;source-dir-name&gt;/secret</c> — a sibling agent's worktree.
    /// Identical bytes, different meaning, because the link's containing directory moved.</para>
    ///
    /// <para>Requiring the accumulated depth to never go negative is position-INDEPENDENT: a path that
    /// never rises above its own root resolves inside any root it is transplanted into, at equal depth. It
    /// also needs no path comparison, so it cannot inherit the OS-inferred case-sensitivity problem that
    /// makes every name- and path-based exclusion in this area wrong in one direction or the other.</para>
    /// </summary>
    /// <param name="linkDirRelative">The link's own directory, relative to the snapshot root, with
    /// <c>/</c> separators. Empty for the root itself.</param>
    /// <param name="rawTarget">The link target exactly as authored — never resolved.</param>
    internal static bool IsAdmissibleLinkTarget(string linkDirRelative, string rawTarget) {
        if (string.IsNullOrEmpty(rawTarget)) return false;

        // Reject EVERY rooted form, not just fully-qualified absolutes. On Windows `\foo` and `C:foo` are
        // rooted-but-not-fully-qualified and would otherwise reach the depth walk as though relative.
        if (Path.IsPathRooted(rawTarget) || Path.IsPathFullyQualified(rawTarget)) return false;
        if (rawTarget.Length >= 2 && rawTarget[1] == ':') return false;

        // Judged the same way on every platform rather than by the host's own rules. A backslash is a legal
        // FILENAME character on Unix, so `\foo` there is a relative name that Path.IsPathRooted calls
        // relative — but the identical bytes are rooted on Windows. Deciding per-host would make
        // admissibility depend on where the copy happens to run, which is the class of reasoning that made
        // the original exclusion wrong. Over-rejecting a pathological Unix filename is the safe direction.
        if (rawTarget[0] is '/' or '\\') return false;

        // Admissible only under BOTH tokenizations. A single merged one is NOT conservative in both
        // directions: treating `\` as a separator turns `..\..\x` into two levels up (safely
        // over-rejecting), but it also splits `a\b\c` into three levels of DEPTH, which masks a following
        // `../..` — and on Unix, where `a\b\c` is one directory name, that target really does escape. So
        // `a\b\c/../../outside` must be judged with slash-only parsing too, and rejected because it is
        // unsafe there.
        return NeverEscapes(linkDirRelative, rawTarget, UnixSeparators)
            && NeverEscapes(linkDirRelative, rawTarget, WindowsSeparators);
    }

    static readonly char[] UnixSeparators = ['/'];

    static readonly char[] WindowsSeparators = ['/', '\\'];

    /// <summary>Whether the depth below the root stays non-negative at every step of the link's own
    /// directory followed by its raw target, under one given tokenization.</summary>
    static bool NeverEscapes(string linkDirRelative, string rawTarget, char[] separators) {
        var depth = 0;

        foreach (var segment in new[] { linkDirRelative, rawTarget })
            foreach (var part in segment.Split(separators, StringSplitOptions.RemoveEmptyEntries)) {
                if (part == "..") { if (--depth < 0) return false; }
                else if (part != ".") depth++;
            }

        return true;
    }

    /// <summary>Copies <paramref name="source"/> into <paramref name="dest"/> without ever following a
    /// link and without descending into the destination.
    ///
    /// <para>Replaces the original <c>CopyDirectory</c>, which was broken three ways at once: it recursed
    /// into its own destination until the path length blew up (so standalone snapshot creation had never
    /// once completed), <c>File.Copy</c> materialised a symlink's TARGET (so a source containing a link to
    /// <c>~/.ssh</c> would write real credentials into the agent's worktree as ordinary files,
    /// indistinguishable from repository content), and its destination exclusion was a name match that is
    /// wrong in one direction or the other under either case semantics.</para>
    ///
    /// <para><b>Guarantee, and its stated limit.</b> For a source not concurrently written by another
    /// principal, nothing from outside <paramref name="source"/> is materialised here. Classification and
    /// the subsequent read are NOT atomic, and .NET exposes no portable no-follow open
    /// (<c>O_NOFOLLOW</c>/<c>openat</c>) for an AOT-compiled binary to use, so a principal able to swap an
    /// entry between the two can still defeat it. That limitation is accepted deliberately and is stated as
    /// an operator precondition under "Daemon" in the README ("Snapshotting a workspace that isn't a git
    /// repo") — keep the two in step. It is not closable at this layer.</para>
    /// </summary>
    void CopySnapshotTree(string source, string dest, string markerName) =>
        CopySnapshotLevel(source, dest, relative: "", markerName);

    void CopySnapshotLevel(string source, string dest, string relative, string markerName) {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source)) {
            var name = Path.GetFileName(entry);

            // Any type, every level: a `.git` FILE (`gitdir: …`) is repository control data just as much as
            // the directory is, and copying one makes this snapshot's own `git init` re-initialise the
            // repository it names — committing outside the snapshot entirely.
            if (IsGitEntryName(name)) continue;

            // Never copy this invocation's own marker. EXACT name only — the claim file is deliberately not
            // matched by prefix here: it lives in the worktrees root, which the marker check below already
            // excludes wholesale, so a prefix rule would buy nothing and would silently drop a legitimate
            // source file that happened to be named `.kcap-claim-notes`.
            if (name == markerName) continue;

            // Classify BEFORE touching it. File.Copy would materialise a link's target, and recursing
            // through a directory link would copy the target tree and can cycle without bound.
            var attrs = File.GetAttributes(entry);
            var destPath = Path.Combine(dest, name);

            if (attrs.HasFlag(FileAttributes.ReparsePoint)) {
                RecreateLinkIfAdmissible(entry, destPath, relative, name, attrs);
                continue;
            }

            if (!attrs.HasFlag(FileAttributes.Directory)) {
                CopyRegularFile(entry, destPath);
                continue;
            }

            // A directory holding this invocation's marker IS the destination's parent. Detected by READING
            // the directory rather than by comparing its name or path, so it resolves correctly under
            // case-sensitive and case-insensitive semantics alike — nothing is inferred from the OS.
            if (File.Exists(Path.Combine(entry, markerName))) continue;

            Directory.CreateDirectory(destPath);
            CopySnapshotLevel(entry, destPath, CombineRelative(relative, name), markerName);
        }
    }

    void RecreateLinkIfAdmissible(
            string entry, string destPath, string relative, string name, FileAttributes attrs) {
        // LinkTarget does not resolve, so a dangling link still reports its target — which is the case that
        // matters, since we judge the target rather than where it currently points.
        var target = new FileInfo(entry).LinkTarget ?? new DirectoryInfo(entry).LinkTarget;

        if (target is null || !IsAdmissibleLinkTarget(relative, target)) {
            LogSkippedLink(CombineRelative(relative, name), target ?? "<unreadable>");

            return;
        }

        // Recreated as a LINK carrying the same raw target — never resolved, never followed, so no
        // out-of-source bytes are ever written. A chain passing through a skipped link simply dangles
        // inside the snapshot rather than reaching out of it.
        //
        // The KIND is preserved from the source entry's own attributes rather than always creating a
        // directory link. Windows records file-vs-directory in the reparse point itself, so a file symlink
        // recreated as a directory link is wrong there — unusable, or a failure during creation. The
        // attribute is read from the link, so a dangling link keeps whatever kind it was authored with.
        if (attrs.HasFlag(FileAttributes.Directory)) Directory.CreateSymbolicLink(destPath, target);
        else File.CreateSymbolicLink(destPath, target);
    }

    /// <summary>Timeout for copying one ordinary file. Overridable for tests only.</summary>
    internal static TimeSpan CopyEntryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Copies one non-directory, non-link entry, bounded in time.
    ///
    /// <para><b>Why bounded.</b> .NET exposes no portable file-TYPE probe: a FIFO reports exactly the same
    /// <c>FileAttributes</c> (<c>Normal</c>), the same <c>UnixFileMode</c> and the same zero length as an
    /// ordinary empty file — measured, not assumed. So a FIFO or device node placed in the source would
    /// make <c>File.Copy</c> block forever waiting for a writer, wedging the launch. Since the entry cannot
    /// be identified, the COPY is bounded instead: one that does not complete promptly fails the snapshot
    /// closed and names the path, turning an indefinite hang by hostile content into an attributable
    /// refusal.</para>
    ///
    /// <para>The abandoned copy's thread is not cancellable — a blocking open cannot be interrupted — so it
    /// remains parked until a writer appears or the process exits. That is one parked thread per hostile
    /// entry, and the launch aborts, which is strictly better than the whole daemon wedging.</para>
    /// </summary>
    static void CopyRegularFile(string source, string dest) {
        var copy = Task.Run(() => File.Copy(source, dest));

        if (!copy.Wait(CopyEntryTimeout))
            throw new InvalidOperationException($"standalone_snapshot_unreadable_entry: {source}");

        // Re-throw a genuine copy failure rather than letting the wait swallow it.
        copy.GetAwaiter().GetResult();
    }

    static string CombineRelative(string relative, string name) =>
        relative.Length == 0 ? name : relative + "/" + name;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipped link {Path} in standalone snapshot: target {Target} is rooted or leaves the source")]
    partial void LogSkippedLink(string path, string target);
}
