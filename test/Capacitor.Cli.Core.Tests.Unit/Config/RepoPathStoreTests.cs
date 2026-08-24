using Capacitor.Cli.Core.Config;
using Capacitor.Tests.Helpers.Guards;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>
/// Tests for RepoPathStore.
///
/// Since StorePath is fixed for the lifetime of the process (static readonly), all
/// RepoPathStore tests share the same file. They run [NotInParallel] and each test
/// deletes the file before running to start from a clean slate.
/// </summary>
[NotInParallel]
public class RepoPathStoreTests {
    static string ReposJsonPath => Path.Combine(RepoPathStoreGlobalSetup.SharedConfigDir, "repos.json");

    // Delete repos.json before each test so tests start from a clean slate.
    [Before(Test)]
    public void DeleteReposJson() {
        if (File.Exists(ReposJsonPath))
            File.Delete(ReposJsonPath);
        // Also clean up any leftover .tmp file from a previous run
        var tmp = ReposJsonPath + ".tmp";
        if (File.Exists(tmp))
            File.Delete(tmp);
    }

    // ── LoadAsync ────────────────────────────────────────────────────────────

    [Test]
    public async Task Load_WhenFileDoesNotExist_ReturnsEmptyArray() {
        var entries = await RepoPathStore.LoadAsync();

        await Assert.That(entries).IsEmpty();
    }

    [Test]
    public async Task Load_WithMalformedJson_ReturnsEmptyArray() {
        await File.WriteAllTextAsync(ReposJsonPath, "this is not json at all {{{");

        var entries = await RepoPathStore.LoadAsync();

        await Assert.That(entries).IsEmpty();
    }

    [Test]
    public async Task Load_WithEmptyArray_ReturnsEmptyArray() {
        await File.WriteAllTextAsync(ReposJsonPath, "[]");

        var entries = await RepoPathStore.LoadAsync();

        await Assert.That(entries).IsEmpty();
    }

    // ── AddAsync ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Add_WhenFileDoesNotExist_CreatesFileWithEntry() {
        var path = "/tmp/my-project";

        await RepoPathStore.AddAsync(path);

        await Assert.That(File.Exists(ReposJsonPath)).IsTrue();
        var entries = await RepoPathStore.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Add_NewPath_AppearsInLoad() {
        var path = "/tmp/my-project";

        await RepoPathStore.AddAsync(path);

        var entries = await RepoPathStore.LoadAsync();
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        await Assert.That(entries.Any(e => e.Path == normalized)).IsTrue();
    }

    [Test]
    public async Task Add_SamePathTwice_DoesNotCreateDuplicate() {
        var path = "/tmp/my-project";

        await RepoPathStore.AddAsync(path);
        await RepoPathStore.AddAsync(path);

        var entries = await RepoPathStore.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Add_SamePathTwice_UpdatesLastUsed() {
        var path = "/tmp/my-project";

        await RepoPathStore.AddAsync(path);
        var firstEntries = await RepoPathStore.LoadAsync();
        var firstLastUsed = firstEntries[0].LastUsed;

        // Small delay to ensure DateTimeOffset.UtcNow advances
        await Task.Delay(10);

        await RepoPathStore.AddAsync(path);
        var secondEntries = await RepoPathStore.LoadAsync();
        var secondLastUsed = secondEntries[0].LastUsed;

        await Assert.That(secondLastUsed).IsGreaterThan(firstLastUsed);
    }

    [Test]
    public async Task Add_MultiplePaths_AllPresentInLoad() {
        await RepoPathStore.AddAsync("/tmp/project-a");
        await RepoPathStore.AddAsync("/tmp/project-b");
        await RepoPathStore.AddAsync("/tmp/project-c");

        var entries = await RepoPathStore.LoadAsync();

        await Assert.That(entries.Length).IsEqualTo(3);
    }

    // ── Path normalization ────────────────────────────────────────────────────

    [Test]
    public async Task Add_PathWithTrailingSeparator_IsNormalized() {
        var pathWithSep    = "/tmp/my-project" + Path.DirectorySeparatorChar;
        var pathWithoutSep = "/tmp/my-project";

        await RepoPathStore.AddAsync(pathWithSep);

        var entries = await RepoPathStore.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(entries[0].Path).IsEqualTo(Path.GetFullPath(pathWithoutSep));
    }

    [Test]
    public async Task Add_SamePathWithAndWithoutTrailingSeparator_TreatedAsSamePath() {
        var pathWithSep    = "/tmp/my-project" + Path.DirectorySeparatorChar;
        var pathWithoutSep = "/tmp/my-project";

        await RepoPathStore.AddAsync(pathWithSep);
        await RepoPathStore.AddAsync(pathWithoutSep);

        var entries = await RepoPathStore.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
    }

    // ── RemoveAsync ───────────────────────────────────────────────────────────

    [Test]
    public async Task Remove_ExistingPath_ReturnsTrue() {
        var path = "/tmp/my-project";
        await RepoPathStore.AddAsync(path);

        var result = await RepoPathStore.RemoveAsync(path);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task Remove_ExistingPath_PathNoLongerInLoad() {
        var path = "/tmp/my-project";
        await RepoPathStore.AddAsync(path);

        await RepoPathStore.RemoveAsync(path);

        var entries = await RepoPathStore.LoadAsync();
        await Assert.That(entries).IsEmpty();
    }

    [Test]
    public async Task Remove_NonExistentPath_ReturnsFalse() {
        var result = await RepoPathStore.RemoveAsync("/tmp/does-not-exist");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task Remove_OneOfMultiplePaths_OthersRemain() {
        await RepoPathStore.AddAsync("/tmp/project-a");
        await RepoPathStore.AddAsync("/tmp/project-b");
        await RepoPathStore.AddAsync("/tmp/project-c");

        await RepoPathStore.RemoveAsync("/tmp/project-b");

        var entries = await RepoPathStore.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(2);
        await Assert.That(entries.Any(e => e.Path.EndsWith("project-b", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Remove_WhenFileDoesNotExist_ReturnsFalse() {
        // File was already deleted in [Before(Test)] — no AddAsync called
        var result = await RepoPathStore.RemoveAsync("/tmp/nonexistent");

        await Assert.That(result).IsFalse();
    }

    // ── GetSortedPathsAsync ───────────────────────────────────────────────────

    [Test]
    public async Task GetSortedPaths_WhenEmpty_ReturnsEmptyArray() {
        var paths = await RepoPathStore.GetSortedPathsAsync();

        await Assert.That(paths).IsEmpty();
    }

    [Test]
    public async Task GetSortedPaths_ReturnsMostRecentlyUsedFirst() {
        await RepoPathStore.AddAsync("/tmp/project-old");
        await Task.Delay(10);
        await RepoPathStore.AddAsync("/tmp/project-new");

        var paths = await RepoPathStore.GetSortedPathsAsync();

        await Assert.That(paths.Length).IsEqualTo(2);
        await Assert.That(paths[0]).IsEqualTo(Path.GetFullPath("/tmp/project-new"));
        await Assert.That(paths[1]).IsEqualTo(Path.GetFullPath("/tmp/project-old"));
    }

    [Test]
    public async Task GetSortedPaths_AfterReAdding_MovesPathToFront() {
        await RepoPathStore.AddAsync("/tmp/project-a");
        await Task.Delay(10);
        await RepoPathStore.AddAsync("/tmp/project-b");
        await Task.Delay(10);

        // Re-add project-a, which should update its LastUsed and move it to front
        await RepoPathStore.AddAsync("/tmp/project-a");

        var paths = await RepoPathStore.GetSortedPathsAsync();

        await Assert.That(paths[0]).IsEqualTo(Path.GetFullPath("/tmp/project-a"));
        await Assert.That(paths[1]).IsEqualTo(Path.GetFullPath("/tmp/project-b"));
    }

    [Test]
    public async Task GetSortedPaths_ReturnOnlyPaths_NotFullEntries() {
        await RepoPathStore.AddAsync("/tmp/project-x");

        var paths = await RepoPathStore.GetSortedPathsAsync();

        // Verify it's string[], not RepoEntry[]
        await Assert.That(paths.Length).IsEqualTo(1);
        await Assert.That(paths[0]).IsEqualTo(Path.GetFullPath("/tmp/project-x"));
    }

    // ── Worktree resolution (GH #655) ─────────────────────────────────────────

    /// Agent registrations persist the launch path, and review flows launch into the requester's
    /// worktree — the store, not the caller, is where that collapses to the main repository, so
    /// every consumer (server launch dialog, app menu, `kcap repos list`) inherits it.
    [Test]
    public async Task Add_resolves_a_linked_worktree_to_its_main_repository() {
        using var tmp = new TempDir();
        var main = tmp.CreateDir("main");
        tmp.CreateDir("main", ".git", "worktrees", "wt1");
        var wt = tmp.CreateDir("wt");
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "wt1")}\n");

        await RepoPathStore.AddAsync(main);
        await RepoPathStore.AddAsync(wt);

        var entries = await RepoPathStore.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(entries[0].Path).IsEqualTo(Path.GetFullPath(main));
    }

    /// Historical pollution: entries written before the resolution existed, whose worktree
    /// directories are long gone. Read-side resolution collapses them without a migration,
    /// keeping the newest last_used per surviving repository.
    [Test]
    public async Task Load_collapses_dead_worktree_entries_into_their_repository() {
        var older = DateTimeOffset.UtcNow.AddDays(-2);
        var newer = DateTimeOffset.UtcNow.AddDays(-1);
        var polluted = new RepoEntry[] {
            new() { Path = "/gone/repo", LastUsed = older },
            new() { Path = "/gone/repo/.claude/worktrees/leaf", LastUsed = newer },
            new() { Path = "/gone/other", LastUsed = older },
        };
        await File.WriteAllTextAsync(ReposJsonPath, System.Text.Json.JsonSerializer.Serialize(polluted));

        var entries = await RepoPathStore.LoadAsync();

        await Assert.That(entries.Length).IsEqualTo(2);
        // GetFullPath, like every assertion in this class: on Windows a rootless "/gone/repo"
        // normalizes to "<drive>:\gone\repo".
        var repo = entries.Single(e => e.Path == Path.GetFullPath("/gone/repo"));
        await Assert.That(repo.LastUsed).IsEqualTo(newer);
    }
}
