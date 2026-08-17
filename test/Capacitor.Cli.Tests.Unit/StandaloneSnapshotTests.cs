using System.Diagnostics;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>End-to-end behaviour of the standalone snapshot branch, driven through the public
/// <see cref="WorktreeManager.CreateAsync"/> against a source that is deliberately NOT a git repo.
///
/// <para><b>Absence is never asserted with a following API.</b> <c>File.Exists</c>, <c>Directory.Exists</c>
/// and <c>Path.Exists</c> all follow, so a dangling link reports absent and an "it was not copied"
/// assertion passes for the wrong reason. Everything here uses <see cref="EntryNames"/> or
/// <see cref="IsPresent"/>, which read the parent directory and the entry's own attributes.</para></summary>
public class StandaloneSnapshotTests {
    // ---- fixtures -----------------------------------------------------------------------------------

    /// <summary>A source directory that is NOT a git repo, so <c>CreateAsync</c> takes the standalone
    /// branch. Created under a per-test root so an "outside" sibling can be placed next to it.</summary>
    static (string root, string source) MakeNonGitSource() {
        var root = Path.Combine(Path.GetTempPath(), "kcap-standalone-" + Guid.NewGuid().ToString("N")[..8]);
        var source = Path.Combine(root, "proj");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "README.md"), "hello");

        return (root, source);
    }

    static WorktreeManager NewManager() =>
        new(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);

    /// <summary>Entry names directly under a directory, WITHOUT following anything.</summary>
    static string[] EntryNames(string dir) =>
        Directory.Exists(dir)
            ? [..Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Where(n => n is not null)!]
            : [];

    /// <summary>Whether an entry exists at this path, including a DANGLING link.</summary>
    static bool IsPresent(string path) {
        try {
            _ = File.GetAttributes(path);

            return true;
        } catch (FileNotFoundException) {
            return false;
        } catch (DirectoryNotFoundException) {
            return false;
        }
    }

    static bool IsLink(string path) =>
        IsPresent(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    static string? LinkTargetOf(string path) =>
        new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;

    /// <summary>Windows needs Developer Mode or elevation to create a symlink, so these assert POSIX
    /// behaviour where the daemon's worktrees actually live. Skipped rather than adapted: a Windows variant
    /// that silently could not create the link would be a test that passes by doing nothing.</summary>
    static void SkipUnlessPosixSymlinks() =>
        Skip.Unless(!OperatingSystem.IsWindows(),
            "POSIX symlink semantics — Windows symlink creation needs Developer Mode or elevation.");

    /// <summary>Fixture git. Exit codes are CHECKED: an ignored failure here silently changes which
    /// creation path <c>CreateAsync</c> selects.</summary>
    static void Git(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"fixture `git {string.Join(' ', args)}` failed: {stderr}");
    }

    static string GitCapture(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        return stdout;
    }

    /// <summary>Commits reachable in a repo. `rev-parse HEAD` is NOT usable as a commitless probe: on an
    /// empty repo it prints the unresolvable name "HEAD" to stdout and fails, so a Trim()-is-empty check
    /// silently never holds. This counts instead.</summary>
    static string CommitCount(string repo) =>
        GitCapture(repo, "rev-list", "--count", "--all").Trim();

    static string[] CommittedPaths(string worktree) =>
        [..GitCapture(worktree, "ls-tree", "-r", "--name-only", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    static void Cleanup(string root) {
        try { Directory.Delete(root, true); } catch { }
    }

    // ---- 1: it completes at all --------------------------------------------------------------------

    /// <summary>The regression test for the recursion, and the positive control for every "not copied"
    /// assertion below: if creation silently produced nothing, this fails first.
    ///
    /// <para>Asserts a known file is in <c>HEAD</c> rather than merely that a commit exists — a snapshot
    /// that committed an empty tree would satisfy the weaker claim.</para></summary>
    [Test]
    public async Task Standalone_creation_completes_and_commits_the_source_content() {
        var (root, source) = MakeNonGitSource();
        try {
            var worktree = await NewManager().CreateAsync(source);

            await Assert.That(worktree.IsStandalone).IsTrue();
            await Assert.That(CommittedPaths(worktree.Path)).Contains("README.md");
        } finally {
            Cleanup(root);
        }
    }

    // ---- 2/3: outside links are not materialised ----------------------------------------------------

    /// <summary>A link to an outside FILE must not bring the file's bytes in.
    ///
    /// <para>The regular file carrying the same sentinel is the positive control: without it, "the secret
    /// is not in the snapshot" would pass just as well for a copy that produced nothing at all.</para>
    /// </summary>
    [Test]
    public async Task Outside_file_link_is_not_materialised() {
        SkipUnlessPosixSymlinks();
        var (root, source) = MakeNonGitSource();
        try {
            const string secret = "SENTINEL-PRIVATE-KEY";
            var outside = Path.Combine(root, "outside-secret.txt");
            File.WriteAllText(outside, secret);
            File.WriteAllText(Path.Combine(source, "control.txt"), secret);
            File.CreateSymbolicLink(Path.Combine(source, "leak.txt"), outside);

            await Assert.That(File.Exists(outside)).IsTrue().Because("fixture control: the target exists");

            var worktree = await NewManager().CreateAsync(source);

            await Assert.That(File.ReadAllText(Path.Combine(worktree.Path, "control.txt"))).IsEqualTo(secret)
                .Because("positive control — an ordinary file with the same bytes IS copied");
            await Assert.That(EntryNames(worktree.Path)).DoesNotContain("leak.txt")
                .Because("the link entry itself must not be recreated, its target being outside the source");
            await Assert.That(IsPresent(Path.Combine(worktree.Path, "leak.txt"))).IsFalse();
        } finally {
            Cleanup(root);
        }
    }

    /// <summary>A link to an outside DIRECTORY must not bring the target tree in, and must not be walked.
    /// </summary>
    [Test]
    public async Task Outside_directory_link_is_not_materialised() {
        SkipUnlessPosixSymlinks();
        var (root, source) = MakeNonGitSource();
        try {
            const string secret = "SENTINEL-IN-OUTSIDE-TREE";
            var outsideDir = Path.Combine(root, "outside-tree");
            Directory.CreateDirectory(outsideDir);
            File.WriteAllText(Path.Combine(outsideDir, "id_rsa"), secret);

            // Positive control: an ordinary sibling directory of real content, so a no-op copy cannot pass.
            var ordinary = Path.Combine(source, "docs");
            Directory.CreateDirectory(ordinary);
            File.WriteAllText(Path.Combine(ordinary, "guide.md"), "real content");

            Directory.CreateSymbolicLink(Path.Combine(source, "creds"), outsideDir);

            await Assert.That(File.Exists(Path.Combine(outsideDir, "id_rsa"))).IsTrue()
                .Because("fixture control: the outside tree exists");

            var worktree = await NewManager().CreateAsync(source);

            await Assert.That(File.ReadAllText(Path.Combine(worktree.Path, "docs", "guide.md")))
                .IsEqualTo("real content").Because("positive control — ordinary directories ARE copied");
            await Assert.That(EntryNames(worktree.Path)).DoesNotContain("creds");
            await Assert.That(IsPresent(Path.Combine(worktree.Path, "creds", "id_rsa"))).IsFalse()
                .Because("the target tree must never be materialised through the link");
        } finally {
            Cleanup(root);
        }
    }

    // ---- 4/5/6: link handling -----------------------------------------------------------------------

    /// <summary>A directory link pointing at its own ancestor must terminate rather than recurse.
    ///
    /// <para>Also asserts the cycle link WAS recreated. Without that, an implementation which simply
    /// dropped every link would satisfy this test while failing the one below.</para></summary>
    [Test]
    public async Task Link_cycle_terminates_and_the_link_is_recreated() {
        SkipUnlessPosixSymlinks();
        var (root, source) = MakeNonGitSource();
        try {
            var nested = Path.Combine(source, "nested");
            Directory.CreateDirectory(nested);
            // nested/loop -> .. : stays within the root at every step, so it is admissible AND cyclic.
            Directory.CreateSymbolicLink(Path.Combine(nested, "loop"), "..");

            var worktree = await NewManager().CreateAsync(source);

            await Assert.That(IsLink(Path.Combine(worktree.Path, "nested", "loop"))).IsTrue()
                .Because("an admissible link is recreated as a link — dropping every link must not pass");
        } finally {
            Cleanup(root);
        }
    }

    /// <summary>A legitimate internal link survives, as a link, with its raw target intact.</summary>
    [Test]
    public async Task Internal_relative_link_survives_as_a_link() {
        SkipUnlessPosixSymlinks();
        var (root, source) = MakeNonGitSource();
        try {
            var releases = Path.Combine(source, "releases", "v2");
            Directory.CreateDirectory(releases);
            File.WriteAllText(Path.Combine(releases, "payload.txt"), "v2 payload");
            Directory.CreateSymbolicLink(Path.Combine(source, "current"), "releases/v2");

            var worktree = await NewManager().CreateAsync(source);
            var current = Path.Combine(worktree.Path, "current");

            await Assert.That(IsLink(current)).IsTrue().Because("it must stay a link, not become a copy");
            await Assert.That(LinkTargetOf(current)).IsEqualTo("releases/v2")
                .Because("the raw target is preserved verbatim");
            await Assert.That(File.ReadAllText(Path.Combine(current, "payload.txt"))).IsEqualTo("v2 payload")
                .Because("and it still resolves to real content inside the snapshot");
        } finally {
            Cleanup(root);
        }
    }

    /// <summary>The relocation bug: a link that leaves the root and re-enters by the source directory's own
    /// name resolves inside the source, but lands in a SIBLING worktree once recreated at the snapshot's
    /// depth.
    ///
    /// <para>The sentinel is planted at the exact sibling path the transplanted target would reach, BEFORE
    /// the call. Asserting merely that "nothing resolves to a sibling" would pass when nothing was ever
    /// there — which is how this bug survives a careless test.</para></summary>
    [Test]
    public async Task Escape_and_reenter_link_is_skipped() {
        SkipUnlessPosixSymlinks();
        var (root, source) = MakeNonGitSource();
        try {
            const string secret = "SENTINEL-SIBLING-WORKTREE";
            File.WriteAllText(Path.Combine(source, "secret.txt"), "in-source copy");

            // `../proj/secret.txt` from the source root resolves back into the source. From
            // <source>/.capacitor/worktrees/<name> it resolves to <...>/worktrees/proj/secret.txt.
            File.CreateSymbolicLink(Path.Combine(source, "self"), "../proj/secret.txt");

            var siblingDir = Path.Combine(source, ".capacitor", "worktrees", "proj");
            Directory.CreateDirectory(siblingDir);
            File.WriteAllText(Path.Combine(siblingDir, "secret.txt"), secret);

            var worktree = await NewManager().CreateAsync(source);

            await Assert.That(EntryNames(worktree.Path)).DoesNotContain("self")
                .Because("a target that rises above the root is inadmissible even though it re-enters");
            await Assert.That(IsPresent(Path.Combine(worktree.Path, "self"))).IsFalse();
            await Assert.That(File.ReadAllText(Path.Combine(siblingDir, "secret.txt"))).IsEqualTo(secret)
                .Because("fixture control: the sibling payload really was there to be reached");
        } finally {
            Cleanup(root);
        }
    }

    // ---- 7/8: .capacitor handling -------------------------------------------------------------------

    /// <summary>A genuine <c>.Capacitor</c> directory of real source content is preserved. The destination
    /// exclusion is a marker, not a name match, so it cannot mistake this for ours.</summary>
    [Test]
    public async Task Genuine_capacitor_directory_of_source_content_is_preserved() {
        var (root, source) = MakeNonGitSource();
        try {
            var genuine = Path.Combine(source, ".Capacitor");
            Directory.CreateDirectory(genuine);
            File.WriteAllText(Path.Combine(genuine, "product.cfg"), "real product config");

            var worktree = await NewManager().CreateAsync(source);

            // On a case-insensitive volume `.Capacitor` and `.capacitor` are ONE directory, and it is ours;
            // on a case-sensitive one they are distinct and this is genuine content that must survive.
            var expected = Path.Combine(worktree.Path, ".Capacitor", "product.cfg");
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
                await Assert.That(File.ReadAllText(expected)).IsEqualTo("real product config");
            else
                await Assert.That(CommittedPaths(worktree.Path)).Contains("README.md")
                    .Because("case-insensitive volume: the directory is ours, so only creation is asserted");
        } finally {
            Cleanup(root);
        }
    }

    /// <summary>A sibling agent worktree is not copied into the new snapshot, while unrelated content under
    /// the same lowercase <c>.capacitor</c> survives.
    ///
    /// <para>The second half is what stops "drop the whole <c>.capacitor</c> directory" from satisfying
    /// this test for entirely the wrong reason.</para></summary>
    [Test]
    public async Task Sibling_worktree_is_excluded_while_other_capacitor_content_survives() {
        var (root, source) = MakeNonGitSource();
        try {
            var sibling = Path.Combine(source, ".capacitor", "worktrees", "agent-old");
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "other-agent.txt"), "another agent's work");

            var settings = Path.Combine(source, ".capacitor", "settings.json");
            File.WriteAllText(settings, "{\"keep\":true}");

            var worktree = await NewManager().CreateAsync(source);

            await Assert.That(IsPresent(Path.Combine(worktree.Path, ".capacitor", "worktrees", "agent-old")))
                .IsFalse().Because("a sibling agent's worktree is neither ours nor source content");
            await Assert.That(File.ReadAllText(Path.Combine(worktree.Path, ".capacitor", "settings.json")))
                .IsEqualTo("{\"keep\":true}")
                .Because("dropping the whole .capacitor directory must NOT satisfy this test");
        } finally {
            Cleanup(root);
        }
    }

    // ---- 9/11: .git of any type ---------------------------------------------------------------------

    /// <summary>A root <c>.git</c> GITFILE is excluded, and the outside repository it names gains no commit.
    ///
    /// <para>This is a write escape, not a leak: copied in, the snapshot's own <c>git init</c> would
    /// re-initialise the referenced repository and <c>commit</c> into it. Reachable because
    /// <c>IsGitRepoWithCommits</c> is <c>git rev-parse HEAD</c>, which follows a gitfile — so one naming a
    /// COMMITLESS repo fails that check and takes this branch.</para></summary>
    [Test]
    public async Task Root_gitfile_is_excluded_and_the_outside_repository_gains_no_commit() {
        var (root, source) = MakeNonGitSource();
        try {
            var outsideRepo = Path.Combine(root, "outside-repo");
            Directory.CreateDirectory(outsideRepo);
            Git(outsideRepo, "init", "-q");
            var outsideGitDir = Path.Combine(outsideRepo, ".git");

            await Assert.That(CommitCount(outsideRepo)).IsEqualTo("0")
                .Because("fixture control: the outside repo is commitless BEFORE the call");

            File.WriteAllText(Path.Combine(source, ".git"), $"gitdir: {outsideGitDir}\n");

            var worktree = await NewManager().CreateAsync(source);

            await Assert.That(worktree.IsStandalone).IsTrue()
                .Because("a gitfile naming a commitless repo fails rev-parse, so this IS the standalone path");
            // The snapshot legitimately HAS a .git — its own, from `git init`. What must not survive is the
            // copied gitfile, so assert the shape: a real directory, not a file naming somewhere else.
            await Assert.That(Directory.Exists(Path.Combine(worktree.Path, ".git"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(worktree.Path, ".git"))).IsFalse()
                .Because("a copied gitfile would leave .git as a FILE pointing at the outside repository");
            await Assert.That(CommittedPaths(worktree.Path)).Contains("README.md")
                .Because("creation still succeeds with the known file committed");
            await Assert.That(CommitCount(outsideRepo)).IsEqualTo("0")
                .Because("the outside repository must have gained NO commit");
        } finally {
            Cleanup(root);
        }
    }

    /// <summary>A <c>.git</c> SYMLINK is excluded too — same control data, different entry type.
    ///
    /// <para>The target is deliberately RELATIVE and inside the source, so the link rule would happily
    /// admit it and only the <c>.git</c> name rule can exclude it. An earlier version of this test pointed
    /// the link at an absolute outside path, which the link rule already rejects — so it passed with the
    /// <c>.git</c> exclusion entirely disabled and proved nothing.</para></summary>
    [Test]
    public async Task Git_symlink_is_excluded() {
        SkipUnlessPosixSymlinks();
        var (root, source) = MakeNonGitSource();
        try {
            // A real repository directory living INSIDE the source, under an ordinary name.
            var innerRepo = Path.Combine(source, "vendored");
            Directory.CreateDirectory(innerRepo);
            Git(innerRepo, "init", "-q");

            Directory.CreateSymbolicLink(Path.Combine(source, ".git"), "vendored/.git");

            var worktree = await NewManager().CreateAsync(source);

            await Assert.That(CommittedPaths(worktree.Path)).Contains("README.md");
            await Assert.That(EntryNames(worktree.Path).Count(n => n == ".git")).IsEqualTo(1)
                .Because("exactly one .git — the snapshot's own, from git init");
            await Assert.That(IsLink(Path.Combine(worktree.Path, ".git"))).IsFalse()
                .Because("the copied link must not have survived as the snapshot's .git");
        } finally {
            Cleanup(root);
        }
    }

    // ---- 10: chain through a skipped link ------------------------------------------------------------

    /// <summary>An admissible prefix link is recreated; only the outside-pointing terminal link is dropped,
    /// leaving the chain unreachable rather than resolving out of the snapshot.
    ///
    /// <para>Asserting only unreachability would pass if the implementation dropped the ENTIRE chain, which
    /// is why the prefix link is asserted present.</para></summary>
    [Test]
    public async Task Chain_through_a_skipped_link_is_unreachable_but_the_allowed_prefix_survives() {
        SkipUnlessPosixSymlinks();
        var (root, source) = MakeNonGitSource();
        try {
            const string secret = "SENTINEL-VIA-CHAIN";
            var outsideDir = Path.Combine(root, "outside-chain");
            Directory.CreateDirectory(outsideDir);
            File.WriteAllText(Path.Combine(outsideDir, "token"), secret);

            var hop = Path.Combine(source, "hop");
            Directory.CreateDirectory(hop);
            // hop/out -> outside  (inadmissible, dropped)
            Directory.CreateSymbolicLink(Path.Combine(hop, "out"), outsideDir);
            // via -> hop          (admissible, recreated)
            Directory.CreateSymbolicLink(Path.Combine(source, "via"), "hop");

            var worktree = await NewManager().CreateAsync(source);

            await Assert.That(IsLink(Path.Combine(worktree.Path, "via"))).IsTrue()
                .Because("the admissible prefix link must survive — dropping the whole chain must not pass");
            await Assert.That(EntryNames(Path.Combine(worktree.Path, "hop"))).DoesNotContain("out")
                .Because("only the outside-pointing terminal link is dropped");
            await Assert.That(IsPresent(Path.Combine(worktree.Path, "via", "out", "token"))).IsFalse()
                .Because("the chain must not resolve out of the snapshot");
        } finally {
            Cleanup(root);
        }
    }

    // ---- 12: destination chain links -----------------------------------------------------------------

    /// <summary>A <c>.capacitor</c> or <c>worktrees</c> component that is a link is refused fail-closed,
    /// and nothing is created through it.
    ///
    /// <para>The "nothing was created" half matters because validation performed AFTER creation would still
    /// throw and would still pass a test that only asserted the exception.</para></summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Reparse_point_in_the_destination_chain_is_refused(bool dangling) {
        SkipUnlessPosixSymlinks();
        var (root, source) = MakeNonGitSource();
        try {
            var elsewhere = Path.Combine(root, "elsewhere");
            if (!dangling) Directory.CreateDirectory(elsewhere);

            Directory.CreateSymbolicLink(Path.Combine(source, ".capacitor"), elsewhere);

            await Assert.That(async () => await NewManager().CreateAsync(source))
                .Throws<InvalidOperationException>();
            await Assert.That(EntryNames(elsewhere)).IsEmpty()
                .Because("nothing may be created THROUGH the link — a post-creation check would also throw");
        } finally {
            Cleanup(root);
        }
    }

    /// <summary>Same rule one level down: a linked <c>worktrees</c> is refused.</summary>
    [Test]
    public async Task Reparse_point_at_the_worktrees_level_is_refused() {
        SkipUnlessPosixSymlinks();
        var (root, source) = MakeNonGitSource();
        try {
            var elsewhere = Path.Combine(root, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            Directory.CreateDirectory(Path.Combine(source, ".capacitor"));
            Directory.CreateSymbolicLink(Path.Combine(source, ".capacitor", "worktrees"), elsewhere);

            await Assert.That(async () => await NewManager().CreateAsync(source))
                .Throws<InvalidOperationException>();
            await Assert.That(EntryNames(elsewhere)).IsEmpty();
        } finally {
            Cleanup(root);
        }
    }

    // ---- 13: occupied destination --------------------------------------------------------------------

    /// <summary>A pre-existing ordinary destination directory is refused and left untouched.
    ///
    /// <para>Two failures in one: <c>Directory.CreateDirectory</c> is a no-op on an existing directory, so
    /// the snapshot would overlay a tree we never created and the rollback would then delete it wholesale;
    /// and control data already sitting there escapes the SOURCE-side <c>.git</c> exclusion entirely,
    /// putting <c>git init</c>/<c>commit</c> back outside the snapshot.</para></summary>
    [Test]
    public async Task Occupied_destination_is_refused_and_left_untouched() {
        var (root, source) = MakeNonGitSource();
        try {
            var outsideRepo = Path.Combine(root, "outside-repo");
            Directory.CreateDirectory(outsideRepo);
            Git(outsideRepo, "init", "-q");

            var occupied = Path.Combine(source, ".capacitor", "worktrees", "taken");
            Directory.CreateDirectory(occupied);
            File.WriteAllText(Path.Combine(occupied, "precious.txt"), "not ours to delete");
            File.WriteAllText(Path.Combine(occupied, ".git"),
                $"gitdir: {Path.Combine(outsideRepo, ".git")}\n");

            await Assert.That(async () => await NewManager().CreateAsync(source, "taken"))
                .Throws<InvalidOperationException>();

            await Assert.That(File.ReadAllText(Path.Combine(occupied, "precious.txt")))
                .IsEqualTo("not ours to delete").Because("rollback must not delete a tree we never created");
            await Assert.That(IsPresent(Path.Combine(occupied, ".git"))).IsTrue()
                .Because("the pre-existing control data is untouched");
            await Assert.That(CommitCount(outsideRepo)).IsEqualTo("0")
                .Because("and nothing was committed into the repository it names");
        } finally {
            Cleanup(root);
        }
    }

    // ---- 14: claim ownership --------------------------------------------------------------------------

    /// <summary>The claim's EXISTENCE excludes a second caller — not the brief non-shared handle.
    ///
    /// <para>Deterministic by construction. The winner is held after the claim file exists but before the
    /// destination does, and only then does the second caller attempt acquisition. At that instant the
    /// winner's handle is already closed, so the only thing that can refuse the second caller is the file
    /// being there — which is exactly what <c>FileMode.CreateNew</c> provides.</para>
    ///
    /// <para>An earlier version merely started both callers behind a pre-claim barrier. That passed even
    /// with the mode weakened to <c>FileMode.Create</c>, because the loser was then refused by the winner's
    /// transient <c>FileShare.None</c> handle instead — a scheduling accident, not the property under
    /// test.</para></summary>
    [Test, NotInParallel]
    public async Task The_claim_files_existence_excludes_a_second_caller() {
        var (root, source) = MakeNonGitSource();
        var claimed = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        try {
            WorktreeManager.SnapshotPostClaimHook = async () => {
                claimed.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            };

            var manager = NewManager();
            var winner = Task.Run(() => manager.CreateAsync(source, "contended"));

            await claimed.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var worktrees = Path.Combine(source, ".capacitor", "worktrees");
            await Assert.That(IsPresent(Path.Combine(worktrees, WorktreeManager.ClaimPrefix + "contended")))
                .IsTrue().Because("fixture control: the claim really was taken before the second attempt");
            await Assert.That(IsPresent(Path.Combine(worktrees, "contended"))).IsFalse()
                .Because("fixture control: the destination does not exist, so only the claim can refuse");

            // Cleared so the second caller is not itself parked by the hook.
            WorktreeManager.SnapshotPostClaimHook = null;

            var loser = await Assert.That(async () => await manager.CreateAsync(source, "contended"))
                .Throws<InvalidOperationException>();
            await Assert.That(loser!.Message).Contains("standalone_snapshot_name_in_use")
                .Because("refusal must come from the claim, not the occupied-destination check");

            release.TrySetResult();
            var built = await winner;
            await Assert.That(CommittedPaths(built.Path)).Contains("README.md")
                .Because("the winner's snapshot must be intact");
        } finally {
            WorktreeManager.SnapshotPostClaimHook = null;
            release.TrySetResult();
            Cleanup(root);
        }
    }

    /// <summary>Two callers racing from the same starting line still yield exactly one winner.</summary>
    [Test, NotInParallel]
    public async Task Concurrent_same_name_creates_yield_one_winner() {
        var (root, source) = MakeNonGitSource();
        var arrived = 0;
        var bothArrived = new TaskCompletionSource();
        try {
            WorktreeManager.SnapshotPreClaimHook = async () => {
                if (Interlocked.Increment(ref arrived) == 2) bothArrived.TrySetResult();
                await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            };

            var manager = NewManager();
            var a = Task.Run(() => manager.CreateAsync(source, "raced"));
            var b = Task.Run(() => manager.CreateAsync(source, "raced"));

            var outcomes = await Task.WhenAll(
                a.ContinueWith(t => t.IsCompletedSuccessfully ? null : Message(t), TaskScheduler.Default),
                b.ContinueWith(t => t.IsCompletedSuccessfully ? null : Message(t), TaskScheduler.Default));

            await Assert.That(outcomes.Count(m => m is null)).IsEqualTo(1)
                .Because("exactly one caller may own the destination");
            await Assert.That(CommittedPaths(Path.Combine(source, ".capacitor", "worktrees", "raced")))
                .Contains("README.md").Because("the winner's snapshot must be intact");
        } finally {
            WorktreeManager.SnapshotPreClaimHook = null;
            bothArrived.TrySetResult();
            Cleanup(root);
        }
    }

    static string Message(Task t) => t.Exception?.GetBaseException().Message ?? "<no exception>";

    /// <summary>The claim is still held while the loser of a FAILED call is rolling back.
    ///
    /// <para>Deterministic rather than a wall-clock race: a simultaneous-start test cannot exercise this
    /// window, and a timing-based one would be flaky and could pass by luck. If the claim were released in
    /// the build method's own <c>finally</c>, a second caller would acquire here — and the first call's
    /// delayed rollback would then delete the second's freshly created tree.</para></summary>
    [Test, NotInParallel]
    public async Task Claim_is_held_through_rollback() {
        var (root, source) = MakeNonGitSource();
        var inRollback = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        try {
            WorktreeManager.SnapshotFailurePoint = "CopySnapshotTree";
            WorktreeManager.SnapshotRollbackHook = async () => {
                inRollback.TrySetResult();
                await release.Task;
            };

            var manager = NewManager();
            var failing = Task.Run(() => manager.CreateAsync(source, "held"));

            await inRollback.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // The first call is parked inside its rollback, holding the claim.
            WorktreeManager.SnapshotFailurePoint = null;
            await Assert.That(async () => await manager.CreateAsync(source, "held"))
                .Throws<InvalidOperationException>()
                .Because("the claim must still exclude a same-name caller during rollback");

            release.TrySetResult();
            await Assert.That(async () => await failing).Throws<InvalidOperationException>();

            // Once released, the name is usable again and the snapshot that follows is intact.
            var after = await manager.CreateAsync(source, "held");
            await Assert.That(CommittedPaths(after.Path)).Contains("README.md");
        } finally {
            WorktreeManager.SnapshotFailurePoint = null;
            WorktreeManager.SnapshotRollbackHook = null;
            release.TrySetResult();
            Cleanup(root);
        }
    }

    /// <summary>A caller that LOSES the claim performs no destination cleanup.
    ///
    /// <para>The rollback is unconditional on the destination path, so a loser falling through it would
    /// delete the winner's directory — a claim that made things strictly worse than no claim.</para>
    /// </summary>
    [Test]
    public async Task Claim_loser_leaves_the_winners_destination_untouched() {
        var (root, source) = MakeNonGitSource();
        try {
            var manager = NewManager();
            var winner = await manager.CreateAsync(source, "owned");

            // Re-take the claim by hand to force the next call down the loser path.
            var claimPath = Path.Combine(
                source, ".capacitor", "worktrees", WorktreeManager.ClaimPrefix + "owned");
            File.WriteAllText(claimPath, "");

            await Assert.That(async () => await manager.CreateAsync(source, "owned"))
                .Throws<InvalidOperationException>();

            await Assert.That(CommittedPaths(winner.Path)).Contains("README.md")
                .Because("the loser must not have deleted the winner's tree");
        } finally {
            Cleanup(root);
        }
    }

    // ---- 15: name validation ---------------------------------------------------------------------------

    /// <summary>A name that is not a single safe path component is refused before anything is created.
    /// Defense-in-depth: both real callers omit <c>name</c>, but the method is public.</summary>
    [Test]
    [Arguments("../evil")]
    [Arguments("nested/child")]
    [Arguments("..")]
    [Arguments(".")]
    [Arguments("")]
    public async Task Unsafe_names_are_refused(string name) {
        var (root, source) = MakeNonGitSource();
        try {
            await Assert.That(async () => await NewManager().CreateAsync(source, name))
                .Throws<InvalidOperationException>();
            await Assert.That(IsPresent(Path.Combine(root, "evil"))).IsFalse()
                .Because("no escaped path may be created");
        } finally {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Absolute_name_is_refused() {
        var (root, source) = MakeNonGitSource();
        var absolute = Path.Combine(root, "absolute-escape");
        try {
            await Assert.That(async () => await NewManager().CreateAsync(source, absolute))
                .Throws<InvalidOperationException>();
            await Assert.That(IsPresent(absolute)).IsFalse();
        } finally {
            Cleanup(root);
        }
    }

    // ---- 16: marker lifetime ---------------------------------------------------------------------------

    /// <summary>The marker and claim are cleaned up on success, and a user file that merely resembles a
    /// marker does not suppress its own directory.</summary>
    [Test]
    public async Task Bookkeeping_files_are_cleaned_up_and_a_lookalike_is_not_honoured() {
        var (root, source) = MakeNonGitSource();
        try {
            // A user file whose name is marker-SHAPED but is not this invocation's marker.
            var decoy = Path.Combine(source, "data");
            Directory.CreateDirectory(decoy);
            File.WriteAllText(Path.Combine(decoy, ".kcap-snapshot-exclude-deadbeef"), "not ours");
            File.WriteAllText(Path.Combine(decoy, "real.txt"), "must survive");

            var worktree = await NewManager().CreateAsync(source, "tidy");

            await Assert.That(File.ReadAllText(Path.Combine(worktree.Path, "data", "real.txt")))
                .IsEqualTo("must survive")
                .Because("only this invocation's EXACT marker name suppresses a directory");

            var worktreeRoot = Path.Combine(source, ".capacitor", "worktrees");
            await Assert.That(EntryNames(worktreeRoot).Where(n => n.StartsWith(".kcap-", StringComparison.Ordinal))).IsEmpty()
                .Because("marker and claim are both released once the snapshot is built");
        } finally {
            Cleanup(root);
        }
    }

    /// <summary>Cleanup also happens on failure, so a crashed launch does not permanently block its name
    /// through a leaked marker.</summary>
    [Test, NotInParallel]
    public async Task Bookkeeping_files_are_cleaned_up_on_failure() {
        var (root, source) = MakeNonGitSource();
        try {
            WorktreeManager.SnapshotFailurePoint = "CopySnapshotTree";

            await Assert.That(async () => await NewManager().CreateAsync(source, "doomed"))
                .Throws<InvalidOperationException>();

            var worktreeRoot = Path.Combine(source, ".capacitor", "worktrees");
            await Assert.That(EntryNames(worktreeRoot).Where(n => n.StartsWith(".kcap-", StringComparison.Ordinal))).IsEmpty()
                .Because("a failed launch must not leak its marker or claim");
            await Assert.That(IsPresent(Path.Combine(worktreeRoot, "doomed"))).IsFalse()
                .Because("the claimant's own partial tree is rolled back");
        } finally {
            WorktreeManager.SnapshotFailurePoint = null;
            Cleanup(root);
        }
    }

    // ---- link kind, claim lookalike, and special files ------------------------------------------------

    /// <summary>An admissible FILE link is recreated as a link that resolves to file content.
    ///
    /// <para><b>This does NOT verify the link KIND, and cannot here.</b> Windows records file-vs-directory
    /// in the reparse point itself, which is the whole reason the production code branches on the source
    /// attributes — but POSIX symlinks carry no type, so <c>Directory.CreateSymbolicLink</c> and
    /// <c>File.CreateSymbolicLink</c> are indistinguishable on this platform. Mutation-checked and
    /// confirmed: forcing the directory API still passes this test. The Windows leg cannot cover it either,
    /// because creating a symlink there needs Developer Mode or elevation, which CI does not have — hence
    /// <see cref="SkipUnlessPosixSymlinks"/>. The kind branch is therefore reasoned, not verified; this test
    /// guards the weaker property that an admissible file link survives and resolves.</para></summary>
    [Test]
    public async Task An_admissible_file_link_is_recreated_as_a_file_link() {
        SkipUnlessPosixSymlinks();
        var (root, source) = MakeNonGitSource();
        try {
            File.WriteAllText(Path.Combine(source, "target.txt"), "payload");
            File.CreateSymbolicLink(Path.Combine(source, "alias.txt"), "target.txt");

            var worktree = await NewManager().CreateAsync(source);
            var alias = Path.Combine(worktree.Path, "alias.txt");

            await Assert.That(IsLink(alias)).IsTrue();
            await Assert.That(File.GetAttributes(alias).HasFlag(FileAttributes.Directory)).IsFalse()
                .Because("a file link recreated as a directory link is wrong on Windows");
            await Assert.That(File.ReadAllText(alias)).IsEqualTo("payload");
        } finally {
            Cleanup(root);
        }
    }

    /// <summary>A source file that merely LOOKS like the daemon's claim bookkeeping is ordinary content and
    /// must be copied.
    ///
    /// <para>The real claim lives in the worktrees root, which the marker already excludes wholesale, so a
    /// prefix rule over the whole tree would buy nothing and silently drop this.</para></summary>
    [Test]
    public async Task A_source_file_named_like_a_claim_is_preserved() {
        var (root, source) = MakeNonGitSource();
        try {
            var docs = Path.Combine(source, "docs");
            Directory.CreateDirectory(docs);
            File.WriteAllText(Path.Combine(docs, ".kcap-claim-notes"), "ordinary content");

            var worktree = await NewManager().CreateAsync(source);

            await Assert.That(File.ReadAllText(Path.Combine(worktree.Path, "docs", ".kcap-claim-notes")))
                .IsEqualTo("ordinary content")
                .Because("only the daemon's own bookkeeping directory is excluded, not a name prefix");
        } finally {
            Cleanup(root);
        }
    }


    // ---- special files ---------------------------------------------------------------------------------

    /// <summary>A FIFO in the source neither hangs the launch nor is opened.
    ///
    /// <para>Reachable AT REST, which is why it matters: this branch runs on non-git sources and copies the
    /// live tree, so a FIFO can sit in a plain directory, an extracted archive, or a commitless repository
    /// with no concurrent writer involved. <c>File.Copy</c> on one blocks forever waiting for a writer
    /// (measured), and no portable API can tell it from an empty regular file — same attributes, same mode,
    /// same zero length. The zero-length rule is what avoids opening it.</para>
    ///
    /// <para>The test has its own watchdog rather than relying on the suite's: a regression here HANGS, and
    /// a hang reports as a timeout somewhere else entirely rather than as this assertion.</para></summary>
    [Test]
    public async Task A_fifo_in_the_source_neither_hangs_nor_is_opened() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX FIFO semantics.");
        var (root, source) = MakeNonGitSource();
        try {
            var fifo = Path.Combine(source, "pipe");
            using (var mk = Process.Start(new ProcessStartInfo("mkfifo", fifo))!) {
                mk.WaitForExit();
                Skip.Unless(mk.ExitCode == 0, "mkfifo unavailable");
            }
            await Assert.That(IsPresent(fifo)).IsTrue().Because("fixture control: the FIFO exists");

            var create = Task.Run(() => NewManager().CreateAsync(source));
            var finished = await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(60))) == create;

            await Assert.That(finished).IsTrue()
                .Because("a special file must never block the copy — this is the regression that hangs");

            var worktree = await create;
            await Assert.That(CommittedPaths(worktree.Path)).Contains("README.md")
                .Because("positive control — the snapshot really was built, not merely non-hanging");

            var copied = Path.Combine(worktree.Path, "pipe");
            await Assert.That(IsPresent(copied)).IsTrue();
            await Assert.That(new FileInfo(copied).Length).IsEqualTo(0)
                .Because("it degrades to an empty ordinary file — never opened, never materialised");
        } finally {
            Cleanup(root);
        }
    }

    /// <summary>An ordinary EMPTY file still round-trips, so the zero-length rule cannot be satisfied by
    /// dropping empty files instead of creating them.</summary>
    [Test]
    public async Task An_empty_regular_file_is_still_copied() {
        var (root, source) = MakeNonGitSource();
        try {
            File.WriteAllText(Path.Combine(source, "empty.txt"), "");

            var worktree = await NewManager().CreateAsync(source);

            await Assert.That(IsPresent(Path.Combine(worktree.Path, "empty.txt"))).IsTrue();
            await Assert.That(new FileInfo(Path.Combine(worktree.Path, "empty.txt")).Length).IsEqualTo(0);
        } finally {
            Cleanup(root);
        }
    }
}
