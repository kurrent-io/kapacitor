using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Vendor MCP config must be excluded along the ancestor chain of the execution cwd, not only at the
/// repository root.
///
/// <para><b>Every test here carries a positive control.</b> A containment test that never produced the
/// file it claims to exclude passes for the wrong reason, and this surface has produced that mistake
/// repeatedly. Where the control is a second build with a different cwd, the assertion is that the SAME
/// fixture yields the file — so "absent" can only mean the exclusion acted.</para>
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class BorrowedSnapshotExclusionScopeTests {
    // ---------- fixture ----------

    sealed record Fixture(TempDir Tmp, TempDirHandle Source, TempDirHandle SnapshotRoot) : IDisposable {
        public void Dispose() => Tmp.Dispose();
    }

    static Fixture NewFixture(params (string Path, string Content)[] tracked) {
        var tmp = new TempDir();
        var source = tmp.CreateDir("source");
        var snapshotRoot = tmp.CreateDir("snapshot");

        Git(source, "init", "-q");
        Git(source, "config", "user.email", "test@example.com");
        Git(source, "config", "user.name", "Test");
        // A file at the root so the repo has content independent of the paths under test.
        source.CreateFile("README.md", "readme");
        foreach (var (path, content) in tracked) source.CreateFile(path.Replace('/', Path.DirectorySeparatorChar), content);
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "fixture");

        return new Fixture(tmp, source, snapshotRoot);
    }

    static WorktreeManager NewManager(Fixture fixture) =>
        new(new DaemonConfig { WorktreeRoot = fixture.SnapshotRoot },
            NullLogger<WorktreeManager>.Instance);

    static async Task<WorktreeInfo> SnapshotAsync(Fixture fixture, string relativeCwd) =>
        await NewManager(fixture).CreateBorrowedSnapshotAsync(
            fixture.Source,
            relativeCwd.Length == 0
                ? fixture.Source
                : fixture.Source.PathTo(relativeCwd.Replace('/', Path.DirectorySeparatorChar)),
            null, CancellationToken.None);

    static bool ExistsInSnapshot(WorktreeInfo snapshot, string relative) =>
        File.Exists(Path.Combine(
            snapshot.SnapshotRoot!, relative.Replace('/', Path.DirectorySeparatorChar)));

    static void Git(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
    }

    static string GitCapture(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return stdout;
    }

    /// <summary>Whether the volume backing the temp directory distinguishes case. Probed, never inferred
    /// from the OS: a case-sensitive APFS volume on macOS and a case-insensitive mount on Linux both
    /// exist, and several assertions here are only meaningful on one side of that.</summary>
    static bool TempIsCaseSensitive() {
        using var tmp = new TempDir();

        tmp.CreateFile("probe");
        return !File.Exists(tmp.PathTo("PROBE"));
    }

    // ---------- 1. Codex sub-cwd, with a root-cwd positive control ----------

    [Test]
    public async Task Codex_config_below_the_root_is_excluded_when_the_cwd_is_that_directory() {
        using var fixture = NewFixture(("src/.codex/config.toml", "[mcp_servers.x]\ncommand = \"/bin/sh\"\n"));

        var snapshot = await SnapshotAsync(fixture, "src");
        try {
            await Assert.That(ExistsInSnapshot(snapshot, "src/.codex/config.toml")).IsFalse()
                .Because("Codex layers .codex/config.toml from the repository root down to the cwd, so a "
                       + "root-scoped list leaves the cwd's own layer live");
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    [Test]
    public async Task Control_the_same_codex_fixture_survives_a_root_cwd_build() {
        using var fixture = NewFixture(("src/.codex/config.toml", "[mcp_servers.x]\ncommand = \"/bin/sh\"\n"));

        var snapshot = await SnapshotAsync(fixture, "");
        try {
            await Assert.That(ExistsInSnapshot(snapshot, "src/.codex/config.toml")).IsTrue()
                .Because("without this the exclusion test above could pass because the fixture never "
                       + "produced the file, or because sub-directories are dropped for some other reason");
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    // ---------- 2. .github/mcp.json at the root ----------

    [Test]
    public async Task Copilot_github_mcp_json_is_excluded_at_the_root() {
        using var fixture = NewFixture(
            (".github/mcp.json", "{\"mcpServers\":{}}"),
            (".github/workflows/ci.yml", "name: ci"));

        // The control is on the SOURCE side: prove the file is really tracked and would be copied,
        // rather than inferring it from a surviving sibling (which only proves .github/ was populated).
        await Assert.That(GitCapture(fixture.Source, "ls-files", "-co", "--exclude-standard"))
            .Contains(".github/mcp.json");

        var snapshot = await SnapshotAsync(fixture, "");
        try {
            await Assert.That(ExistsInSnapshot(snapshot, ".github/mcp.json")).IsFalse()
                .Because("Copilot CLI reads .github/mcp.json; the list carried .github/copilot/mcp.json, "
                       + "a different path, so this was unprotected at every snapshot root");
            await Assert.That(ExistsInSnapshot(snapshot, ".github/workflows/ci.yml")).IsTrue()
                .Because("the exclusion is path-scoped, not a blanket drop of .github/");
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    // ---------- 3. an intermediate directory on the chain ----------

    [Test]
    public async Task Config_in_an_intermediate_ancestor_is_excluded() {
        using var fixture = NewFixture(("a/.mcp.json", "{}"), ("a/b/keep.txt", "keep"));

        var snapshot = await SnapshotAsync(fixture, "a/b");
        try {
            await Assert.That(ExistsInSnapshot(snapshot, "a/.mcp.json")).IsFalse()
                .Because("the whole chain root..cwd is covered, not just its two endpoints");
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    [Test]
    public async Task Control_the_same_intermediate_fixture_survives_a_root_cwd_build() {
        using var fixture = NewFixture(("a/.mcp.json", "{}"), ("a/b/keep.txt", "keep"));

        var snapshot = await SnapshotAsync(fixture, "");
        try {
            await Assert.That(ExistsInSnapshot(snapshot, "a/.mcp.json")).IsTrue();
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    // ---------- 4. a sibling of the cwd is NOT excluded ----------

    [Test]
    public async Task Sibling_of_the_cwd_is_not_excluded() {
        using var fixture = NewFixture(("b/.mcp.json", "{}"), ("a/keep.txt", "keep"));

        var snapshot = await SnapshotAsync(fixture, "a");
        try {
            await Assert.That(ExistsInSnapshot(snapshot, "b/.mcp.json")).IsTrue()
                .Because("no supported vendor discovers config in a sibling of its cwd. Excluding it "
                       + "anyway would strip content the launch cannot reach — this repository's own "
                       + "committed kcap/.mcp.json among it");
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    // ---------- 5. case-sensitive sibling AND the collision it used to cause ----------

    [Test]
    public async Task Case_varying_sibling_survives_and_the_build_succeeds_on_a_case_sensitive_volume() {
        if (!TempIsCaseSensitive()) {
            // Not skipped silently: on a case-insensitive volume `a` and `A` ARE one directory, so the
            // property under test does not exist there and asserting it would be meaningless.
            return;
        }

        using var fixture = NewFixture(("a/.mcp.json", "{}"), ("A/.mcp.json", "{}"));

        // Both tracked deliberately. An earlier revision folded ASCII case unconditionally, which
        // collapsed these onto ONE canonical candidate and made the review-context collision check
        // refuse every launch of the repository — a launch-refusal primitive handed to a hostile branch.
        // Tracking only `A` would not reproduce that.
        var snapshot = await SnapshotAsync(fixture, "a");
        try {
            await Assert.That(ExistsInSnapshot(snapshot, "a/.mcp.json")).IsFalse();
            await Assert.That(ExistsInSnapshot(snapshot, "A/.mcp.json")).IsTrue()
                .Because("on a case-sensitive volume A/ is a genuine sibling the vendor cannot discover");
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    // ---------- 7. --show-prefix framing, against real git ----------

    [Test]
    public async Task Show_prefix_bytes_agree_with_the_paths_ls_files_reports() {
        using var fixture = NewFixture(("src/cli/keep.txt", "keep"));
        var cwd = fixture.Source.PathTo("src", "cli");

        var prefix = await WorktreeManager.ReadGitRelativeCwdAsync(
            fixture.Source, cwd, CancellationToken.None);

        // The oracle is git's OWN listing, not our plan builder — a builder validated against itself
        // would pass with an identically wrong derivation.
        await Assert.That(GitCapture(fixture.Source, "ls-files", "-co", "--exclude-standard"))
            .Contains(prefix + "/keep.txt");
    }

    [Test]
    public async Task Show_prefix_at_the_repository_root_is_empty() {
        using var fixture = NewFixture();

        var prefix = await WorktreeManager.ReadGitRelativeCwdAsync(
            fixture.Source, fixture.Source, CancellationToken.None);

        await Assert.That(prefix).IsEqualTo("");
    }

    // ---------- 8. root prefix goes through the real command output ----------

    [Test]
    public async Task Root_prefix_expands_to_exactly_the_canonical_list() {
        // Through the parser, from the bytes git actually emits at the root ("\n") — not from a
        // pre-normalized "", which would bypass the framing rules entirely.
        var prefix = WorktreeManager.ParseGitRelativeCwd("\n"u8);
        var plan = WorktreeManager.PlanSnapshotExclusions(prefix, caseSensitive: true);

        await Assert.That(plan.VendorConfigPaths.Length)
            .IsEqualTo(WorktreeManager.WorkspaceMcpConfigPaths.Length);
        foreach (var path in WorktreeManager.WorkspaceMcpConfigPaths)
            await Assert.That(plan.VendorConfigPaths).Contains(path);
    }

    [Test]
    [Arguments("")]              // no trailing LF
    [Arguments("src/")]          // no trailing LF
    [Arguments("src\n")]         // no trailing slash on a non-root prefix
    [Arguments("src/\n\n")]      // more than one LF
    [Arguments("src/\r\n")]      // CR anywhere
    [Arguments("/\n")]           // slash-only remainder
    public async Task Malformed_show_prefix_output_is_refused(string raw) {
        await Assert.That(() => WorktreeManager.ParseGitRelativeCwd(Encoding.UTF8.GetBytes(raw)))
            .Throws<InvalidOperationException>();
    }

    // ---------- 9/10. non-ASCII prefixes, at the pinned parse order ----------

    [Test]
    public async Task Nfc_non_ascii_prefix_is_refused_only_on_a_case_insensitive_destination() {
        var prefix = WorktreeManager.ParseGitRelativeCwd("café/\n"u8);

        // Case-sensitive: admitted, compared byte-exactly, no folding involved.
        var plan = WorktreeManager.PlanSnapshotExclusions(prefix, caseSensitive: true);
        await Assert.That(plan.VendorConfigPaths).Contains("café/.mcp.json");

        // Case-insensitive: refused, because that volume also equates pairs the ASCII-only matcher
        // would miss, and proving the equivalence would mean a second Unicode-aware matcher.
        await Assert.That(() => WorktreeManager.PlanSnapshotExclusions(prefix, caseSensitive: false))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Nfd_prefix_fails_at_normalization_not_at_the_non_ascii_rule() {
        // Normalization (NormalizeRelativePath) runs BEFORE the case-dependent rule, so that rule can
        // assume an NFC operand. The consequence is that an NFD prefix never reaches it.
        var nfd = "café/\n";

        await Assert.That(() => WorktreeManager.ParseGitRelativeCwd(Encoding.UTF8.GetBytes(nfd)))
            .Throws<InvalidOperationException>();
    }

    // ---------- 11. caps at the boundary ----------

    [Test]
    public async Task Depth_at_the_cap_is_admitted_and_one_over_is_refused() {
        var atCap = string.Join('/', Enumerable.Range(0, WorktreeManager.MaxCwdDepth).Select(i => "d" + i));
        var overCap = atCap + "/one-too-many";

        var plan = WorktreeManager.PlanSnapshotExclusions(atCap, caseSensitive: true);
        await Assert.That(plan.VendorConfigPaths.Length)
            .IsEqualTo((WorktreeManager.MaxCwdDepth + 1) * WorktreeManager.WorkspaceMcpConfigPaths.Length);

        await Assert.That(() => WorktreeManager.PlanSnapshotExclusions(overCap, caseSensitive: true))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Aggregate_path_bytes_are_capped_independently_of_depth() {
        // Aggregate bytes grow O(depth squared) because each level repeats the whole prefix, so a
        // shallow-but-wide cwd can blow the byte budget while passing the depth check.
        var wide = string.Join('/', Enumerable.Range(0, 8).Select(_ => new string('x', 250)));

        await Assert.That(() => WorktreeManager.PlanSnapshotExclusions(wide, caseSensitive: true))
            .Throws<InvalidOperationException>();
    }

    // ---------- 12/13. reserved index policy ----------

    [Test]
    public async Task Tracked_config_below_the_root_does_not_show_as_a_deletion() {
        using var fixture = NewFixture(("src/.mcp.json", "{}"), ("src/keep.txt", "keep"));

        var snapshot = await SnapshotAsync(fixture, "src");
        try {
            await Assert.That(ExistsInSnapshot(snapshot, "src/.mcp.json")).IsFalse();
            // The skip-worktree bit is what keeps the reviewer's `git status` clean; without it the
            // reviewer sees a deletion kcap performed and can legitimately file a finding about it.
            await Assert.That(GitCapture(snapshot.SnapshotRoot!, "status", "--porcelain").Trim())
                .IsEqualTo("");
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    [Test]
    public async Task Staged_but_uncommitted_config_does_not_fail_the_build() {
        using var fixture = NewFixture(("src/keep.txt", "keep"));
        // In the SOURCE index but not in HEAD — so it is absent from the destination's index, which is
        // checked out at HEAD. Intersecting the skip-worktree batch against the SOURCE index instead
        // would batch this path and fail update-index on a perfectly legitimate snapshot.
        fixture.Source.CreateFile("src/.mcp.json", "{}");
        Git(fixture.Source, "add", "src/.mcp.json");

        var snapshot = await SnapshotAsync(fixture, "src");
        try {
            await Assert.That(ExistsInSnapshot(snapshot, "src/.mcp.json")).IsFalse();
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    // ---------- 14. review context and containment move together ----------

    [Test]
    public async Task Config_excluded_below_the_root_is_still_reachable_to_the_reviewer() {
        const string hostile = "{\"mcpServers\":{\"x\":{\"command\":\"/bin/sh\"}}}";
        using var fixture = NewFixture(("src/.kiro/settings/mcp.json", hostile));

        var snapshot = await SnapshotAsync(fixture, "src");
        try {
            await Assert.That(ExistsInSnapshot(snapshot, "src/.kiro/settings/mcp.json")).IsFalse()
                .Because("Kiro was measured spawning the declared command at session setup");

            // Contained is not enough: the change under review may BE this file. If exclusion widened
            // without the extractor widening with it, a hostile config one directory down would be
            // contained AND invisible — a reviewer could return clean on exactly the change the
            // exclusion exists to defend against.
            var manifest = Directory.EnumerateFiles(
                    snapshot.ReviewContextRoot!, "manifest.json", SearchOption.AllDirectories)
                .Select(File.ReadAllText)
                .Single();
            await Assert.That(manifest).Contains("src/.kiro/settings/mcp.json");
            await Assert.That(manifest).Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(hostile)));
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    // ---------- 15. rooted / escaping cwd ----------

    [Test]
    public async Task Cwd_outside_the_source_is_refused() {
        using var fixture = NewFixture();
        using var outside = new TempDir();
        await Assert.That(async () => await NewManager(fixture).CreateBorrowedSnapshotAsync(
                fixture.Source, outside.Path, null, CancellationToken.None))
            .Throws<InvalidOperationException>();
    }

    // ---------- 16. symlinked snapshot root resolving inside the source ----------

    [Test]
    public async Task Snapshot_root_symlinked_inside_the_source_is_refused() {
        if (OperatingSystem.IsWindows()) {
            // Windows needs Developer Mode or elevation to create a symlink.
            return;
        }

        using var fixture = NewFixture();
        var inside = fixture.Source.PathTo("nested-worktrees");
        Directory.CreateDirectory(inside);
        using var tmp = new TempDir();
        var link = tmp.PathTo("link");
        Directory.CreateSymbolicLink(link, inside);

        // Control: the lexical comparison alone passes this — the link's own path is outside the
        // source, which is exactly why resolving is required rather than nice to have.
        await Assert.That(link.StartsWith(fixture.Source, StringComparison.Ordinal)).IsFalse();

        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = link }, NullLogger<WorktreeManager>.Instance);
        await Assert.That(async () => await manager.CreateBorrowedSnapshotAsync(
                fixture.Source, fixture.Source, null, CancellationToken.None))
            .Throws<InvalidOperationException>()
            .Because("Claude Code's .mcp.json lookup walks upward past the git root, so a snapshot "
                   + "under the source would sit beneath the source's own config");
    }

    // ---------- descendants of a reserved path are index entries too ----------

    /// <summary>A repository CAN track <c>.mcp.json/child</c> — the config pathname as a DIRECTORY — and
    /// that child is a real index entry, which is why the index policy marks every non-<c>Unrelated</c>
    /// match rather than only <c>Exact</c>.
    /// <para>On the BORROWED path this state is unreachable: the review-context extractor refuses a
    /// reserved path that is a directory before the index policy is ever reached. That is pre-existing
    /// fail-closed behaviour, pinned here so a later change cannot quietly relax it into the very
    /// deletion-and-restore case the index fix defends against.</para></summary>
    [Test]
    public async Task A_reserved_path_tracked_as_a_directory_refuses_a_borrowed_build() {
        using var fixture = NewFixture((".mcp.json/child", "{}"), ("keep.txt", "keep"));

        await Assert.That(async () => await SnapshotAsync(fixture, ""))
            .Throws<InvalidOperationException>();
    }

    /// <summary>The same fixture through the path that has NO review context, where the descendant does
    /// reach the index policy. Marked skip-worktree, it leaves a clean status; marked only on
    /// <c>Exact</c>, it would read as a deletion the reviewer could file a finding about — and an
    /// ordinary git operation could restore it, rebuilding a live vendor-config tree.</summary>
    [Test]
    public async Task A_tracked_descendant_of_a_reserved_path_does_not_show_as_a_deletion() {
        using var fixture = NewFixture(("keep.txt", "keep"));
        var manager = NewManager(fixture);

        // The target is an existing snapshot — SyncFromSourceAsync replaces contents, it does not create
        // the tree. Built before the descendant exists, so the sync below is the operation under test.
        var snapshot = await SnapshotAsync(fixture, "");
        try {
            var target = snapshot.SnapshotRoot!;

            fixture.Source.CreateFile(".mcp.json/child", "{}");
            Git(fixture.Source, "add", "-A");
            Git(fixture.Source, "commit", "-q", "-m", "config as a directory");

            await manager.SyncFromSourceAsync(
                fixture.Source, fixture.Source, target, [], CancellationToken.None);

            await Assert.That(File.Exists(Path.Combine(target, ".mcp.json", "child"))).IsFalse();
            await Assert.That(GitCapture(target, "status", "--porcelain").Trim()).IsEqualTo("");
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    // ---------- the prefix must belong to the repository whose manifest it filters ----------

    [Test]
    public async Task A_cwd_in_a_nested_repository_is_refused() {
        using var fixture = NewFixture(("keep.txt", "keep"));
        // A nested repository inside the source tree. `rev-parse` run there reports the NESTED repo's
        // work-tree top, so its prefix is in a different namespace from the source's ls-files output —
        // and matching one against the other is exactly the invariant this derivation exists to hold.
        var nested = fixture.Source.PathTo("vendored");
        Directory.CreateDirectory(nested);
        Git(nested, "init", "-q");

        await Assert.That(async () => await NewManager(fixture).CreateBorrowedSnapshotAsync(
                fixture.Source, nested, null, CancellationToken.None))
            .Throws<InvalidOperationException>();
    }

    // ---------- snapshot root reaching the source through an ANCESTOR link ----------

    [Test]
    public async Task Snapshot_root_reaching_the_source_through_an_ancestor_symlink_is_refused() {
        if (OperatingSystem.IsWindows()) {
            return;
        }

        using var fixture = NewFixture();
        // The link is an ANCESTOR of the configured root, and the configured root's own deepest existing
        // component is an ordinary directory. Resolving only that deepest component returns the lexical
        // path and the containment check passes — which is the bug this covers.
        var inside = fixture.Source.PathTo("nested", "existing");
        Directory.CreateDirectory(inside);
        using var tmp = new TempDir();
        var link = tmp.PathTo("link");
        Directory.CreateSymbolicLink(link, Path.Combine(fixture.Source, "nested"));

        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = Path.Combine(link, "existing") },
            NullLogger<WorktreeManager>.Instance);
        await Assert.That(async () => await manager.CreateBorrowedSnapshotAsync(
                fixture.Source, fixture.Source, null, CancellationToken.None))
            .Throws<InvalidOperationException>();
    }

    // ---------- 17. the non-borrowed sync path ----------

    [Test]
    public async Task Non_borrowed_sync_excludes_ancestor_config_for_its_source_cwd() {
        using var fixture = NewFixture(("src/.mcp.json", "{}"), ("src/keep.txt", "keep"));
        var manager = NewManager(fixture);

        var snapshot = await SnapshotAsync(fixture, "src");
        try {
            // Re-sync through the public overload, which now REQUIRES a source-side cwd. The overloads
            // it replaced took only a target-side path, leaving no way to obtain the git prefix except
            // by re-deriving it from the target filesystem.
            await manager.SyncFromSourceAsync(
                fixture.Source, fixture.Source.PathTo("src"),
                snapshot.SnapshotRoot!, [], CancellationToken.None);

            await Assert.That(ExistsInSnapshot(snapshot, "src/.mcp.json")).IsFalse();
            await Assert.That(ExistsInSnapshot(snapshot, "src/keep.txt")).IsTrue();
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }

    // ---------- 18. the refresh path carries the persisted prefix ----------

    [Test]
    public async Task Refresh_reuses_the_persisted_prefix_and_keeps_ancestor_config_out() {
        using var fixture = NewFixture(("src/keep.txt", "keep"));
        var manager = NewManager(fixture);

        var snapshot = await SnapshotAsync(fixture, "src");
        try {
            await Assert.That(snapshot.GitRelativeCwd).IsEqualTo("src");

            // A config appearing between rounds is the case that matters: the refresh must exclude it
            // using the prefix computed at CREATION, since it has only a target-side path of its own.
            fixture.Source.CreateFile("src/.mcp.json", "{}");
            fixture.Source.CreateFile(".mcp.json", "{}");
            Git(fixture.Source, "add", "-A");
            Git(fixture.Source, "commit", "-q", "-m", "adds config");

            await manager.SyncBorrowedSnapshotFromSourceAsync(
                fixture.Source, snapshot.SnapshotRoot!, snapshot.GitRelativeCwd!,
                [], snapshot.ReviewContextRoot!, CancellationToken.None);

            await Assert.That(ExistsInSnapshot(snapshot, "src/.mcp.json")).IsFalse();
            await Assert.That(ExistsInSnapshot(snapshot, ".mcp.json")).IsFalse();
            await Assert.That(ExistsInSnapshot(snapshot, "src/keep.txt")).IsTrue();
        } finally { await WorktreeManager.RemoveAsync(snapshot); }
    }
}
