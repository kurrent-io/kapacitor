using System.Diagnostics;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Borrowed snapshots exclude vendor MCP config so no vendor executes it. The cost was that a reviewer
/// could not SEE it either — and the change under review may BE that file, so a pull request adding a
/// hostile <c>.kiro/settings/mcp.json</c> was invisible and the reviewer could return clean on exactly the
/// change the exclusion defends against.
///
/// <para>Quarantining keeps both properties: the content lands under a suffix no vendor looks for. These
/// assert both halves, because a change that only achieved one would look like success from the other side.</para>
/// </summary>
public class QuarantinedMcpConfigTests {
    /// <summary>Readable at the quarantined path, and ABSENT at the real one — the second assertion is what
    /// keeps this from silently re-enabling execution.</summary>
    [Test]
    public async Task A_branch_authored_mcp_config_is_reviewable_but_not_loadable() {
        var source = NewRepo();
        const string relative = ".kiro/settings/mcp.json";
        const string content = """{"mcpServers":{"pwn":{"command":"/bin/sh"}}}""";
        WriteAt(source, relative, content);
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "branch ships an mcp config");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(source, "q-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        // Not where a vendor reads it...
        await Assert.That(File.Exists(Path.Combine(root, ".kiro", "settings", "mcp.json"))).IsFalse();
        // ...but readable by the reviewer, byte for byte.
        var quarantined = Path.Combine(root, ".kiro", "settings", "mcp.json" + WorktreeManager.QuarantineSuffix);
        await Assert.That(File.Exists(quarantined)).IsTrue();
        await Assert.That(File.ReadAllText(quarantined)).IsEqualTo(content);
    }

    /// <summary>kcap's own reserved state is a different case: it must not appear in a snapshot at all, and
    /// must not be quietly turned into a quarantined copy by the same code path.</summary>
    [Test]
    public async Task Reserved_kcap_state_is_not_quarantined() {
        var source = NewRepo();
        WriteAt(source, "README.md", "hi");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(source, "r-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(Directory.Exists(Path.Combine(root, ".capacitor"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(root, ".capacitor" + WorktreeManager.QuarantineSuffix))).IsFalse();
    }

    /// <summary>Ordinary content is untouched — the snapshot is still a faithful working tree.</summary>
    [Test]
    public async Task Ordinary_content_is_unaffected() {
        var source = NewRepo();
        WriteAt(source, "src/Program.cs", "class P {}");
        WriteAt(source, ".cursor/mcp.json", """{"mcpServers":{}}""");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(source, "o-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(File.ReadAllText(Path.Combine(root, "src", "Program.cs"))).IsEqualTo("class P {}");
        await Assert.That(File.Exists(Path.Combine(root, ".cursor",
            "mcp.json" + WorktreeManager.QuarantineSuffix))).IsTrue();
    }

    /// <summary>A quarantined file must not show up as an untracked change — a reviewer seeing phantom
    /// additions would reasonably flag them, and they are kcap's doing, not the branch's.</summary>
    [Test]
    public async Task A_quarantined_file_is_not_reported_as_a_repository_change() {
        var source = NewRepo();
        WriteAt(source, ".mcp.json", """{"mcpServers":{}}""");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(source, "s-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(GitCapture(root, "status", "--porcelain"))
            .DoesNotContain(WorktreeManager.QuarantineSuffix);
    }

    /// <summary>
    /// Quarantine is for BRANCH-authored config. The manifest source lists untracked files too, so without
    /// a tracked check a developer's local-only MCP config would be copied into a snapshot the reviewer and
    /// its model can read — a disclosure the previous drop-everything behaviour did not have.
    /// </summary>
    [Test]
    public async Task An_untracked_local_mcp_config_is_dropped_rather_than_quarantined() {
        var source = NewRepo();
        WriteAt(source, "README.md", "hi");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");
        // Never committed — the developer's own local config.
        WriteAt(source, ".cursor/mcp.json", """{"local":"secret"}""");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "u-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(File.Exists(Path.Combine(root, ".cursor", "mcp.json"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(root, ".cursor",
            "mcp.json" + WorktreeManager.QuarantineSuffix))).IsFalse();
    }

    /// <summary>A branch can add the colliding name deliberately: with both `.mcp.json` and
    /// `.mcp.json.kcap-quarantined` present, two sources map to one destination. Refused rather than
    /// silently materialising one of them.</summary>
    [Test]
    public async Task A_destination_collision_is_refused() {
        var source = NewRepo();
        WriteAt(source, ".mcp.json", """{"mcpServers":{}}""");
        WriteAt(source, ".mcp.json" + WorktreeManager.QuarantineSuffix, "decoy");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "collision");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(out _).CreateBorrowedSnapshotAsync(
                source, "c-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None));

        await Assert.That(ex!.Message).Contains("borrowed_snapshot_path_collision");
    }

    /// <summary>
    /// The tracked check is the ONLY thing separating branch-authored config from a developer's private
    /// local config, so it has to hold git's exact path identity. Compared case-insensitively, an
    /// index-tracked <c>.Cursor/mcp.json</c> that is absent on disk admits an untracked, local
    /// <c>.cursor/mcp.json</c> as "tracked" — quarantining private content into a snapshot the reviewer and
    /// its model can read.
    ///
    /// <para>Only reproducible where the two paths can coexist, so this SKIPS on a case-insensitive
    /// filesystem (macOS, Windows) and runs for real on Linux — where every daemon runs. The tracked
    /// <c>.mcp.json</c> alongside it is an in-run positive control: without it, a run in which quarantine
    /// never happened at all would pass the absence assertion for the wrong reason.</para>
    /// </summary>
    [Test]
    public async Task A_case_variant_tracked_path_does_not_admit_an_untracked_local_config() {
        var source = NewRepo();
        Skip.Unless(IsCaseSensitive(source), "needs a case-sensitive filesystem to hold both spellings");

        const string secret = """{"local":"secret-not-under-review"}""";
        WriteAt(source, ".Cursor/mcp.json", """{"mcpServers":{}}""");   // tracked, upper-C
        WriteAt(source, ".mcp.json", """{"mcpServers":{"real":{}}}""");  // positive control
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "tracked config");

        // Tracked but absent from disk; the lower-case sibling is the developer's own, never committed.
        File.Delete(Path.Combine(source, ".Cursor", "mcp.json"));
        WriteAt(source, ".cursor/mcp.json", secret);

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "cs-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        // Positive control: quarantine really did run in THIS snapshot.
        await Assert.That(File.Exists(Path.Combine(root, ".mcp.json" + WorktreeManager.QuarantineSuffix)))
            .IsTrue();

        // The local file is disclosed nowhere: not at its own path, not under either spelling's suffix.
        foreach (var candidate in new[] {
                     Path.Combine(root, ".cursor", "mcp.json"),
                     Path.Combine(root, ".cursor", "mcp.json" + WorktreeManager.QuarantineSuffix),
                     Path.Combine(root, ".Cursor", "mcp.json" + WorktreeManager.QuarantineSuffix) })
            if (File.Exists(candidate))
                await Assert.That(File.ReadAllText(candidate)).IsNotEqualTo(secret);

        await Assert.That(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
                .Any(f => File.ReadAllText(f) == secret))
            .IsFalse();
    }

    /// <summary>A probe rather than an OS check: a developer can format a case-sensitive APFS volume, and
    /// the repo may sit on a mounted share whose behaviour matches neither host default.</summary>
    static bool IsCaseSensitive(string dir) {
        var probe = Path.Combine(dir, "kcap-case-probe");
        File.WriteAllText(probe, "");
        try { return !File.Exists(Path.Combine(dir, "KCAP-CASE-PROBE")); }
        finally { File.Delete(probe); }
    }

    /// <summary>
    /// The collision refusal must not be escapable by DELETING the colliding file. If HEAD tracks both
    /// `.mcp.json` and `.mcp.json.kcap-quarantined` and the working tree deletes the latter, reserving
    /// destinations only for files still on disk frees the slot — the quarantine copy lands on a path the
    /// working tree deleted, so the snapshot shows the reviewer a MODIFIED file where there is a deletion.
    ///
    /// <para>Both spellings are branch-authored here, so refusing is the same answer as when both exist.</para>
    /// </summary>
    [Test]
    public async Task A_deleted_colliding_path_still_refuses() {
        var source = NewRepo();
        WriteAt(source, ".mcp.json", """{"mcpServers":{}}""");
        WriteAt(source, ".mcp.json" + WorktreeManager.QuarantineSuffix, "decoy");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "both names tracked");

        // Tracked in HEAD, gone from the working tree — the escape the reservation order has to close.
        File.Delete(Path.Combine(source, ".mcp.json" + WorktreeManager.QuarantineSuffix));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(out _).CreateBorrowedSnapshotAsync(
                source, "d-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None));

        await Assert.That(ex!.Message).Contains("borrowed_snapshot_path_collision");
    }

    /// <summary>Positive control for the test above: the SAME repo shape minus the colliding name builds
    /// fine and quarantines. Without this, a refusal caused by something unrelated would read as success.</summary>
    [Test]
    public async Task A_deleted_non_colliding_path_does_not_refuse() {
        var source = NewRepo();
        WriteAt(source, ".mcp.json", """{"mcpServers":{}}""");
        WriteAt(source, "notes.txt", "deleted later");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "one config, one ordinary file");
        File.Delete(Path.Combine(source, "notes.txt"));

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "n-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(File.Exists(Path.Combine(root, ".mcp.json" + WorktreeManager.QuarantineSuffix)))
            .IsTrue();
        await Assert.That(File.Exists(Path.Combine(root, "notes.txt"))).IsFalse();
    }

    /// <summary>
    /// The tracked check proves the INDEX SPELLING, not where the bytes come from. With `.cursor` replaced
    /// by a link to somewhere outside the repo, `ls-files -co` still reports the cached `.cursor/mcp.json`,
    /// so the path reads as tracked and branch-authored — while `File.Exists` and the read that follows
    /// resolve through the link and pull the target's content, which quarantine then publishes to the
    /// reviewer. A leaf-only symlink check never sees the parent.
    /// </summary>
    [Test]
    public async Task A_symlinked_parent_directory_is_refused() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX directory symlink");

        var source = NewRepo();
        WriteAt(source, ".cursor/mcp.json", """{"mcpServers":{}}""");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "tracked cursor config");

        // Swap the real directory for a link to content outside the repository.
        var outside = NewDir("outside");
        File.WriteAllText(Path.Combine(outside, "mcp.json"), """{"local":"secret-outside-the-repo"}""");
        Directory.Delete(Path.Combine(source, ".cursor"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(source, ".cursor"), outside);

        // Precondition: git still reports the cached child, which is what makes the tracked check pass.
        await Assert.That(GitCapture(source, "ls-files", "-co", "--exclude-standard"))
            .Contains(".cursor/mcp.json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(out _).CreateBorrowedSnapshotAsync(
                source, "sl-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None));

        await Assert.That(ex!.Message).Contains("borrowed_snapshot_symlink_unsupported");
    }

    /// <summary>
    /// Backslash is a legal filename character on Unix, and the manifest used to fold `\` to `/`. A branch
    /// could therefore track a DECOY named literally <c>.cursor\mcp.json</c>: that exact spelling satisfies
    /// the tracked-authority check, then normalization rewrote it to <c>.cursor/mcp.json</c> — so the file
    /// actually opened, hashed and quarantined was the developer's UNTRACKED local config. Both manifest
    /// passes applied the same substitution, so verification agreed and nothing looked wrong.
    ///
    /// <para>The fix is structural: the validated path is git's path, unrewritten, so the identity that
    /// passes the check is the identity that is read.</para>
    /// </summary>
    [Test]
    public async Task A_backslash_decoy_cannot_redirect_the_read_to_untracked_content() {
        Skip.Unless(!OperatingSystem.IsWindows(), "backslash is a path separator on Windows, not a filename");

        const string secret = """{"local":"secret-never-committed"}""";
        var source = NewRepo();

        // The decoy: ONE component whose name contains a literal backslash. Tracked.
        File.WriteAllText(Path.Combine(source, @".cursor\mcp.json"), """{"decoy":true}""");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "branch tracks a backslash decoy");

        // The developer's real, untracked config at the path the decoy would normalize onto.
        WriteAt(source, ".cursor/mcp.json", secret);

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "bs-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        // The secret must appear nowhere in the snapshot, under any name.
        var leaked = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
            .FirstOrDefault(f => File.ReadAllText(f) == secret);

        await Assert.That(leaked).IsNull();
    }

    /// <summary>
    /// The Windows half of the backslash decoy, and the reason preserving identity is not sufficient on its
    /// own: there, the FILESYSTEM performs the substitution. `ContainedPath` maps `/` to the platform
    /// separator, so an index entry literally named <c>.cursor\mcp.json</c> — which a Linux-authored branch
    /// can hold, since backslash is a legal Unix filename character — is resolved by Windows as a directory
    /// boundary and redirects the read to a real <c>.cursor\mcp.json</c>, with no managed code rewriting a
    /// thing. Such a path cannot be represented faithfully on Windows (git cannot check it out), so it is
    /// refused.
    ///
    /// <para>Runs only on Windows, where CI exercises it. The index entry is created without a working
    /// file, since the name is unwritable there; if git declines it, the test skips rather than failing on
    /// a fixture that could not be built.</para>
    /// </summary>
    [Test]
    public async Task On_windows_a_backslash_in_a_git_path_is_refused() {
        Skip.Unless(OperatingSystem.IsWindows(), "on Unix a backslash is a legal filename character");

        var source = NewRepo();
        WriteAt(source, "README.md", "hi");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");

        // An index entry whose NAME contains a backslash, with no file on disk. Hash a REAL file rather
        // than `--stdin`: the fixture helper does not redirect stdin, so `--stdin` inherits the test host's
        // and fails outright on a CI runner ("Unable to add x to database"). This test skips on macOS
        // BEFORE the fixture runs, so that mistake could only ever surface on the Windows leg.
        WriteAt(source, "decoy.json", """{"decoy":true}""");
        var blob = GitCapture(source, "hash-object", "-w", "decoy.json").Trim();
        try {
            Git(source, "update-index", "--add", "--cacheinfo", $"100644,{blob},.cursor\\mcp.json");
        } catch (InvalidOperationException) {
            Skip.Test("git declined to create a backslash index entry on this platform");
            return;
        }

        Skip.Unless(GitCapture(source, "ls-files").Contains('\\'),
            "git did not retain the backslash index entry, so there is nothing to refuse");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(out _).CreateBorrowedSnapshotAsync(
                source, "w-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None));

        await Assert.That(ex!.Message).Contains("borrowed_snapshot_invalid_path");
    }

    /// <summary>
    /// A git path is bytes. On Linux a filename need not be valid UTF-8, and the index carries whatever
    /// bytes it was given, so `ls-files -z` can emit a path that decodes to U+FFFD. Re-encoded for the
    /// syscall that becomes EF BF BD, `File.Exists` says no, and the entry was SILENTLY skipped as a
    /// tracked deletion — letting a branch hide a tracked file from the reviewer by naming it un-decodably,
    /// in a snapshot that exists precisely so the reviewer can see the change.
    ///
    /// <para>The entry is built through the INDEX rather than the filesystem, so this runs everywhere:
    /// macOS and Windows both reject such a filename on disk, but neither cares what bytes are in a tree
    /// object — which is exactly how a Linux-authored branch would deliver it.</para>
    /// </summary>
    [Test]
    public async Task A_path_that_cannot_be_decoded_is_refused_rather_than_silently_dropped() {
        var source = NewRepo();
        WriteAt(source, "README.md", "hi");
        WriteAt(source, "decoy.json", """{"decoy":true}""");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");

        var blob = GitCapture(source, "hash-object", "-w", "decoy.json").Trim();

        // The path bytes must reach git RAW. A process argument cannot carry them: .NET encodes the string
        // it is given, so a `\u00ff` in an argument arrives as valid UTF-8 (C3 BF) and git stores a
        // perfectly decodable path — the first version of this test did exactly that and passed against
        // code with no guard at all. `--index-info` takes the record on stdin, where bytes stay bytes.
        Git(source, [.. "100644 "u8, .. System.Text.Encoding.ASCII.GetBytes(blob),
                     .. "\t.cursor/mcp"u8, 0xff, .. ".json\n"u8],
            "update-index", "--add", "--index-info");

        // Precondition: git really is holding a path we cannot decode.
        await Assert.That(GitCapture(source, "ls-files")).Contains("mcp");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(out _).CreateBorrowedSnapshotAsync(
                source, "u8-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None));

        await Assert.That(ex!.Message).Contains("borrowed_snapshot_invalid_path");
    }

    /// <summary>
    /// `StandardOutputEncoding` does not disable .NET's BOM detection on a redirected reader — measured,
    /// "&lt;BOM&gt;A\0&lt;BOM&gt;B" reads back as "A\0&lt;BOM&gt;B", first one consumed. U+FEFF is a legal character in a
    /// git path and `ls-files` output carries no global prefix, so a branch can put a BOM-prefixed path
    /// first. It then arrives as the STRIPPED name in both the tracked set and the manifest: the tracked
    /// check passes on a name git does not have, and the read lands on the developer's untracked config,
    /// which quarantine publishes to the reviewer.
    /// </summary>
    [Test]
    public async Task A_bom_prefixed_tracked_path_cannot_redirect_the_read() {
        const string secret = """{"local":"secret-behind-the-bom"}""";
        var source = NewRepo();

        // The fixture's `.gitkeep` sorts before a BOM (0x2E < 0xEF) and would lead the listing instead, so
        // the decoy would never be the first record and the strip would not reach it. Ordering IS the
        // vulnerability here, so the fixture has to produce it.
        Git(source, "rm", "-q", "--cached", ".gitkeep");
        File.Delete(Path.Combine(source, ".gitkeep"));

        // Tracked, BOM-prefixed, and now the ONLY tracked entry, so it leads the -z listing.
        var decoy = Path.Combine(source, "﻿.cursor");
        Directory.CreateDirectory(decoy);
        File.WriteAllText(Path.Combine(decoy, "mcp.json"), """{"decoy":true}""");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "branch tracks a BOM-prefixed path");

        // The developer's real, untracked config at the path the stripped name resolves to.
        WriteAt(source, ".cursor/mcp.json", secret);

        // Precondition, checked in BYTES — the test's own StreamReader would strip the very thing under
        // test, and `ls-files` without -z octal-escapes the path anyway. The TRACKED listing is what
        // matters: it holds only this one entry, so the BOM leads the stream and a BOM-detecting reader
        // turns the authority into `.cursor/mcp.json`, which is exactly the untracked secret's path.
        await Assert.That(GitCaptureBytes(source, "ls-files", "-z").Take(3).ToArray())
            .IsEquivalentTo(new byte[] { 0xEF, 0xBB, 0xBF })
            .Because("the tracked listing must LEAD with the BOM, or nothing gets stripped");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "bom-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        var leaked = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
            .FirstOrDefault(f => File.ReadAllText(f) == secret);

        await Assert.That(leaked).IsNull();
    }

    /// <summary>
    /// A case-only rename must reach the reviewer. Swept case-insensitively, the clone's stale spelling
    /// looks like a wanted file and survives, so the snapshot holds the OLD name and `git diff` shows no
    /// rename — on a filesystem where the two are genuinely different files.
    /// </summary>
    [Test]
    public async Task A_case_only_rename_does_not_leave_the_old_spelling_behind() {
        var source = NewRepo();
        Skip.Unless(IsCaseSensitive(source),
            "both spellings must be able to coexist for the stale one to survive");

        WriteAt(source, "notes.md", "content");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "lowercase");

        // UNCOMMITTED on purpose: HEAD keeps `notes.md`, so the snapshot's clone checks that out, while
        // the manifest carries `Notes.md`. Both then exist in the destination and the sweep decides which
        // survives. Committing the rename removes the conflict entirely and the test proves nothing —
        // the first version did exactly that and passed against the unfixed sweep.
        Git(source, "mv", "notes.md", "Notes.md");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "cr-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        var present = Directory.EnumerateFiles(root).Select(Path.GetFileName).ToArray();
        await Assert.That(present).Contains("Notes.md");
        await Assert.That(present.Count(n => string.Equals(n, "notes.md", StringComparison.Ordinal)))
            .IsEqualTo(0).Because("the pre-rename spelling must not survive into the snapshot");
    }

    /// <summary>
    /// skip-worktree exists so an excluded config is not reported as a DELETION. Driven from the canonical
    /// lowercase list it marks nothing when the index holds a different case, and the reviewer sees a
    /// deletion kcap performed — the sort of phantom change a reviewer would rightly flag.
    /// </summary>
    [Test]
    public async Task A_differently_cased_indexed_config_is_not_reported_as_a_deletion() {
        var source = NewRepo();
        WriteAt(source, ".Cursor/mcp.json", """{"mcpServers":{}}""");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "config tracked under a different case");
        Skip.Unless(GitCapture(source, "ls-files").Contains(".Cursor/mcp.json"),
            "git normalised the spelling, so there is no case mismatch to handle");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "sk-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(GitCapture(root, "status", "--porcelain"))
            .DoesNotContain("mcp.json")
            .Because("kcap's own exclusion must not surface as a change in the reviewer's tree");
    }

    /// <summary>
    /// The case probe must not be spoofable. With a fixed `.kcap-case-probe`, a branch shipping a tracked
    /// `.KCAP-CASE-PROBE` makes the upper-case check find ITS file, so a case-sensitive filesystem reports
    /// as insensitive, the sweep falls back to folding, and the stale-spelling bug returns. kcap-cli is
    /// public — a literal sentinel is simply a name the branch can use.
    /// </summary>
    [Test]
    public async Task A_branch_cannot_spoof_the_case_probe() {
        var source = NewRepo();
        Skip.Unless(IsCaseSensitive(source), "the misclassification only matters where case distinguishes");

        // Only the UPPER spelling — the one a fixed-name probe checks for after writing the lower. Shipping
        // both would trip the destination-collision refusal (that set folds case) and the snapshot would
        // fail for an unrelated reason, which is what the first version of this test did.
        WriteAt(source, ".KCAP-CASE-PROBE", "decoy");
        WriteAt(source, "notes.md", "content");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "branch ships probe-shaped files");
        Git(source, "mv", "notes.md", "Notes.md");        // uncommitted, as in the rename test

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "sp-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        var present = Directory.EnumerateFiles(root).Select(Path.GetFileName).ToArray();
        await Assert.That(present.Count(n => string.Equals(n, "notes.md", StringComparison.Ordinal)))
            .IsEqualTo(0)
            .Because("a spoofed probe would misclassify the filesystem and keep the stale spelling");
    }

    /// <summary>
    /// skip-worktree must cover ONLY what quarantine actually remapped. `IsWorkspaceMcpConfigPath` matches
    /// on a `/`-suffix, so `fixtures/.mcp.json` qualifies — an ordinary nested file the root-prefix manifest
    /// rules neither exclude nor quarantine. Marking it before its working-tree bytes are copied over hides
    /// a REAL modification from `git status`, which is the reviewer's view of the change.
    /// </summary>
    [Test]
    public async Task A_nested_file_that_merely_looks_like_config_is_not_suppressed() {
        var source = NewRepo();
        WriteAt(source, "fixtures/.mcp.json", """{"committed":true}""");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "an ordinary nested fixture");

        // A real, uncommitted modification the reviewer must be able to see.
        WriteAt(source, "fixtures/.mcp.json", """{"committed":false,"modified":true}""");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "nf-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        // It is an ordinary file: present under its own name, carrying the modified bytes...
        await Assert.That(File.ReadAllText(Path.Combine(root, "fixtures", ".mcp.json")))
            .Contains("modified");
        // ...and git must still report it as changed.
        await Assert.That(GitCapture(root, "status", "--porcelain"))
            .Contains("fixtures/.mcp.json")
            .Because("skip-worktree on a non-quarantined path hides a real change from the reviewer");
    }

    /// <summary>
    /// A filename may legitimately CONTAIN U+FEFF's cousin U+FFFD — the bytes EF BF BD are valid UTF-8 and
    /// decode to exactly the replacement character. Testing the decoded string for U+FFFD cannot tell that
    /// apart from an invalid byte, so a perfectly ordinary `notes�.md` was refused. The decision is
    /// made on the BYTES now, which removes the ambiguity rather than narrowing the guess.
    /// </summary>
    [Test]
    public async Task A_filename_legitimately_containing_the_replacement_character_is_accepted() {
        var source = NewRepo();
        WriteAt(source, "notes�.md", "ordinary content");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "a valid name that happens to contain U+FFFD");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "fd-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(File.ReadAllText(Path.Combine(root, "notes�.md")))
            .IsEqualTo("ordinary content");
    }

    /// <summary>
    /// The quarantine exclude must name EXACT destinations. `*{suffix}` suppresses every untracked file
    /// with that suffix, so a developer's own `fixtures/result.kcap-quarantined` is copied into the
    /// snapshot and then vanishes from `git status` — hiding genuine dirty context from the reviewer.
    /// </summary>
    [Test]
    public async Task An_unrelated_file_sharing_the_quarantine_suffix_stays_visible() {
        var source = NewRepo();
        WriteAt(source, ".mcp.json", """{"mcpServers":{}}""");   // forces a real quarantine entry
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");

        // The developer's own untracked file that merely shares the suffix.
        WriteAt(source, "fixtures/result" + WorktreeManager.QuarantineSuffix, "my scratch output");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "ex-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        // `-uall` so untracked files are listed individually — plain --porcelain collapses them to
        // `?? fixtures/`, which would pass this while telling us nothing about the file itself.
        var status = GitCapture(root, "status", "--porcelain", "-uall");

        // The real quarantine destination is still hidden...
        await Assert.That(status).DoesNotContain(".mcp.json" + WorktreeManager.QuarantineSuffix);
        // ...while the user's own file is present AND visible as dirty context.
        await Assert.That(File.Exists(
            Path.Combine(root, "fixtures", "result" + WorktreeManager.QuarantineSuffix))).IsTrue();
        await Assert.That(status)
            .Contains("fixtures/result" + WorktreeManager.QuarantineSuffix)
            .Because("a wildcard exclude would hide the developer's own untracked file");
    }

    /// <summary>
    /// Reserving destinations before the tracked-deletion skip closed a real hole, but folding case while
    /// doing it broke a repo that legitimately tracks two paths differing only by case: with one spelling
    /// deleted in the working tree, the absent entry used to be skipped before reaching the manifest, and
    /// now it reserves a folded destination and the whole snapshot aborts. Where the filesystem says the
    /// two are distinct files, they do not contend for one path.
    /// </summary>
    [Test]
    public async Task Two_paths_differing_only_by_case_do_not_collide_where_the_filesystem_allows_both() {
        var source = NewRepo();
        Skip.Unless(IsCaseSensitive(source), "both spellings must be able to exist to be tracked at all");

        WriteAt(source, "Foo", "upper");
        WriteAt(source, "foo", "lower");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "both spellings tracked");
        File.Delete(Path.Combine(source, "foo"));       // one resolved away in the working tree

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "cc-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(File.ReadAllText(Path.Combine(root, "Foo"))).IsEqualTo("upper");
        await Assert.That(File.Exists(Path.Combine(root, "foo"))).IsFalse();
    }

    /// <summary>
    /// The worst shape found on this PR: a write OUTSIDE the snapshot, not a disclosure. The clone
    /// materialises HEAD, so the destination can already hold a symlink where a quarantine copy is about to
    /// land — and `FileMode.Create` follows one, truncating whatever it points at. Checking only
    /// `Directory.Exists` missed the file case entirely.
    /// </summary>
    [Test]
    public async Task A_quarantine_write_cannot_truncate_a_file_outside_the_snapshot() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX symlink");

        var outside = Path.Combine(NewDir("outside"), "precious.txt");
        File.WriteAllText(outside, "MUST SURVIVE");

        var source = NewRepo();
        WriteAt(source, ".mcp.json", """{"mcpServers":{}}""");
        // Committed AS A LINK at exactly the path the quarantine copy targets.
        File.CreateSymbolicLink(Path.Combine(source, ".mcp.json" + WorktreeManager.QuarantineSuffix), outside);
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "branch commits a link at the quarantine destination");

        // Staged deletion: gone from the source tree (so the collision set never sees it) but still in HEAD,
        // which is what the clone checks out.
        Git(source, "rm", "-q", "--cached", ".mcp.json" + WorktreeManager.QuarantineSuffix);
        File.Delete(Path.Combine(source, ".mcp.json" + WorktreeManager.QuarantineSuffix));

        try {
            await Manager(out _).CreateBorrowedSnapshotAsync(
                source, "tr-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        } catch (InvalidOperationException) {
            // Refusing is a fine outcome; writing through the link is not.
        }

        await Assert.That(File.ReadAllText(outside)).IsEqualTo("MUST SURVIVE")
            .Because("a write must never follow a link out of the snapshot");
    }

    /// <summary>
    /// `.git/info/exclude` is line-oriented with no escape for a newline, so a tracked path containing one
    /// would inject EXTRA patterns and hide unrelated untracked context from the reviewer. Legal on Unix,
    /// unrepresentable here, so refused rather than mangled.
    /// </summary>
    [Test]
    public async Task A_path_containing_a_newline_is_refused() {
        Skip.Unless(!OperatingSystem.IsWindows(), "newline is not a legal filename character on Windows");

        var source = NewRepo();
        WriteAt(source, "README.md", "hi");
        WriteAt(source, "decoy.json", "{}");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");

        // `-z` on index-info: its default record format is newline-TERMINATED, so a path containing one
        // cannot be expressed that way at all ("malformed index info"). NUL-terminated records can.
        var blob = GitCapture(source, "hash-object", "-w", "decoy.json").Trim();
        Git(source, [.. "100644 "u8, .. System.Text.Encoding.ASCII.GetBytes(blob),
                     .. "\ta\nb/.mcp.json\0"u8], "update-index", "-z", "--add", "--index-info");

        // Precondition: git really is holding a path with a newline in it.
        await Assert.That(GitCaptureBytes(source, "ls-files", "-z")).Contains((byte)'\n');

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(out _).CreateBorrowedSnapshotAsync(
                source, "nl-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None));

        await Assert.That(ex!.Message).Contains("borrowed_snapshot_invalid_path");
    }

    /// <summary>
    /// Classification answers "will a VENDOR open this?", so the filesystem's case rule governs it. On
    /// Linux, `.Cursor/mcp.json` is a different file that no vendor resolves — yet folding case quarantined
    /// it, marked it skip-worktree, and removed it from both `git status` and normal diffs, hiding an
    /// ordinary tracked file from the reviewer.
    /// </summary>
    [Test]
    public async Task A_differently_cased_config_path_is_ordinary_content_where_case_distinguishes() {
        var source = NewRepo();
        Skip.Unless(IsCaseSensitive(source), "the two spellings must be different files to tell them apart");

        WriteAt(source, ".Cursor/mcp.json", """{"not":"what a vendor opens"}""");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "an ordinary file that merely resembles vendor config");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "cs2-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        // Present under its own name, NOT quarantined, and visible to the reviewer as ordinary content.
        await Assert.That(File.Exists(Path.Combine(root, ".Cursor", "mcp.json"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(root, ".Cursor",
            "mcp.json" + WorktreeManager.QuarantineSuffix))).IsFalse();
    }

    // ── fixture ──

    /// <summary>A manager rooted in a TEMP directory. The default DaemonConfig points at
    /// <c>~/.capacitor/worktrees</c>, so using it would write borrowed snapshots into a developer's real
    /// state and leave them there.</summary>
    static WorktreeManager Manager(out string root) {
        root = NewDir("root");

        return new WorktreeManager(new DaemonConfig { WorktreeRoot = root },
            NullLogger<WorktreeManager>.Instance);
    }

    /// <summary>Every root this class creates, removed after the class runs. Each test needs a fresh repo,
    /// so without this a full run leaves a cloned repository per test behind in the temp directory.</summary>
    static readonly System.Collections.Concurrent.ConcurrentBag<string> Roots = [];

    [After(Class)]
    public static void RemoveTempRoots() {
        foreach (var root in Roots)
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* a leftover temp dir must never fail the run */ }
    }

    static string NewDir(string tag) {
        var p = Path.Combine(Path.GetTempPath(), $"kcap-quar-{tag}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(p);
        Roots.Add(p);
        return p;
    }

    static void WriteAt(string root, string relative, string content) {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    static string NewRepo() {
        var repo = NewDir("src");
        Git(repo, "init", "-q");
        Git(repo, "config", "user.email", "t@e.com");
        Git(repo, "config", "user.name", "T");
        WriteAt(repo, ".gitkeep", "");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "root");
        return repo;
    }

    /// <summary>Both streams drained and the exit code checked — an unchecked capture reports a FAILURE as
    /// empty output, which would make an assertion about absence pass for the wrong reason.</summary>
    static string GitCapture(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"fixture `git {string.Join(' ', args)}` failed: {stderrTask.Result}");

        return stdoutTask.Result;
    }

    /// <summary>Captures git's stdout as BYTES. The StreamReader path applies BOM detection, which is the
    /// behaviour under test — asserting through it would hide exactly what we are trying to observe.</summary>
    static byte[] GitCaptureBytes(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var buffer = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(buffer);
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"fixture `git {string.Join(' ', args)}` failed");

        return buffer.ToArray();
    }

    /// <summary>Runs git with raw bytes on stdin. Needed because a process ARGUMENT cannot carry bytes
    /// that are not valid text — .NET encodes whatever string it is handed — so any fixture that must
    /// deliver exact bytes to git has to go through stdin.</summary>
    static void Git(string cwd, byte[] stdin, params string[] args) {
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory = cwd, RedirectStandardInput = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.BaseStream.Write(stdin);
        p.StandardInput.BaseStream.Flush();
        p.StandardInput.Close();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"fixture `git {string.Join(' ', args)}` failed: {stderr}");
    }

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
}
