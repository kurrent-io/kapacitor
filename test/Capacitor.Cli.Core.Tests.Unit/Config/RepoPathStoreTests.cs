using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>Tests for <see cref="RepoPathStore"/>. Each test gets its own root, so nothing here is
/// shared and nothing needs cleaning up between tests.</summary>
public class RepoPathStoreTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Lazy: injection happens after construction, so Config is not readable from an initializer.
    RepoPathStore Repos => field ??= new RepoPathStore(Config.Root);

    string ReposJsonPath => Config.PathTo("repos.json");

    // ── LoadAsync ────────────────────────────────────────────────────────────

    [Test]
    public async Task Load_WhenFileDoesNotExist_ReturnsEmptyArray() {
        var entries = await Repos.LoadAsync();

        await Assert.That(entries).IsEmpty();
    }

    [Test]
    public async Task Load_WithMalformedJson_ReturnsEmptyArray() {
        await File.WriteAllTextAsync(ReposJsonPath, "this is not json at all {{{");

        var entries = await Repos.LoadAsync();

        await Assert.That(entries).IsEmpty();
    }

    [Test]
    public async Task Load_WithEmptyArray_ReturnsEmptyArray() {
        await File.WriteAllTextAsync(ReposJsonPath, "[]");

        var entries = await Repos.LoadAsync();

        await Assert.That(entries).IsEmpty();
    }

    // ── AddAsync ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Add_WhenFileDoesNotExist_CreatesFileWithEntry() {
        var path = "/tmp/my-project";

        await Repos.AddAsync(path);

        await Assert.That(File.Exists(ReposJsonPath)).IsTrue();
        var entries = await Repos.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Add_NewPath_AppearsInLoad() {
        var path = "/tmp/my-project";

        await Repos.AddAsync(path);

        var entries = await Repos.LoadAsync();
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        await Assert.That(entries.Any(e => e.Path == normalized)).IsTrue();
    }

    [Test]
    public async Task Add_SamePathTwice_DoesNotCreateDuplicate() {
        var path = "/tmp/my-project";

        await Repos.AddAsync(path);
        await Repos.AddAsync(path);

        var entries = await Repos.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Add_SamePathTwice_UpdatesLastUsed() {
        var path = "/tmp/my-project";

        await Repos.AddAsync(path);
        var firstEntries = await Repos.LoadAsync();
        var firstLastUsed = firstEntries[0].LastUsed;

        // Small delay to ensure DateTimeOffset.UtcNow advances
        await Task.Delay(10);

        await Repos.AddAsync(path);
        var secondEntries = await Repos.LoadAsync();
        var secondLastUsed = secondEntries[0].LastUsed;

        await Assert.That(secondLastUsed).IsGreaterThan(firstLastUsed);
    }

    [Test]
    public async Task Add_MultiplePaths_AllPresentInLoad() {
        await Repos.AddAsync("/tmp/project-a");
        await Repos.AddAsync("/tmp/project-b");
        await Repos.AddAsync("/tmp/project-c");

        var entries = await Repos.LoadAsync();

        await Assert.That(entries.Length).IsEqualTo(3);
    }

    // ── Path normalization ────────────────────────────────────────────────────

    [Test]
    public async Task Add_PathWithTrailingSeparator_IsNormalized() {
        var pathWithSep    = "/tmp/my-project" + Path.DirectorySeparatorChar;
        var pathWithoutSep = "/tmp/my-project";

        await Repos.AddAsync(pathWithSep);

        var entries = await Repos.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(entries[0].Path).IsEqualTo(Path.GetFullPath(pathWithoutSep));
    }

    [Test]
    public async Task Add_SamePathWithAndWithoutTrailingSeparator_TreatedAsSamePath() {
        var pathWithSep    = "/tmp/my-project" + Path.DirectorySeparatorChar;
        var pathWithoutSep = "/tmp/my-project";

        await Repos.AddAsync(pathWithSep);
        await Repos.AddAsync(pathWithoutSep);

        var entries = await Repos.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(1);
    }

    // ── RemoveAsync ───────────────────────────────────────────────────────────

    [Test]
    public async Task Remove_ExistingPath_ReturnsTrue() {
        var path = "/tmp/my-project";
        await Repos.AddAsync(path);

        var result = await Repos.RemoveAsync(path);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task Remove_ExistingPath_PathNoLongerInLoad() {
        var path = "/tmp/my-project";
        await Repos.AddAsync(path);

        await Repos.RemoveAsync(path);

        var entries = await Repos.LoadAsync();
        await Assert.That(entries).IsEmpty();
    }

    [Test]
    public async Task Remove_NonExistentPath_ReturnsFalse() {
        var result = await Repos.RemoveAsync("/tmp/does-not-exist");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task Remove_OneOfMultiplePaths_OthersRemain() {
        await Repos.AddAsync("/tmp/project-a");
        await Repos.AddAsync("/tmp/project-b");
        await Repos.AddAsync("/tmp/project-c");

        await Repos.RemoveAsync("/tmp/project-b");

        var entries = await Repos.LoadAsync();
        await Assert.That(entries.Length).IsEqualTo(2);
        await Assert.That(entries.Any(e => e.Path.EndsWith("project-b", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Remove_WhenFileDoesNotExist_ReturnsFalse() {
        var result = await Repos.RemoveAsync("/tmp/nonexistent");

        await Assert.That(result).IsFalse();
    }

    // ── GetSortedPathsAsync ───────────────────────────────────────────────────

    [Test]
    public async Task GetSortedPaths_WhenEmpty_ReturnsEmptyArray() {
        var paths = await Repos.GetSortedPathsAsync();

        await Assert.That(paths).IsEmpty();
    }

    [Test]
    public async Task GetSortedPaths_ReturnsMostRecentlyUsedFirst() {
        await Repos.AddAsync("/tmp/project-old");
        await Task.Delay(10);
        await Repos.AddAsync("/tmp/project-new");

        var paths = await Repos.GetSortedPathsAsync();

        await Assert.That(paths.Length).IsEqualTo(2);
        await Assert.That(paths[0]).IsEqualTo(Path.GetFullPath("/tmp/project-new"));
        await Assert.That(paths[1]).IsEqualTo(Path.GetFullPath("/tmp/project-old"));
    }

    [Test]
    public async Task GetSortedPaths_AfterReAdding_MovesPathToFront() {
        await Repos.AddAsync("/tmp/project-a");
        await Task.Delay(10);
        await Repos.AddAsync("/tmp/project-b");
        await Task.Delay(10);

        // Re-add project-a, which should update its LastUsed and move it to front
        await Repos.AddAsync("/tmp/project-a");

        var paths = await Repos.GetSortedPathsAsync();

        await Assert.That(paths[0]).IsEqualTo(Path.GetFullPath("/tmp/project-a"));
        await Assert.That(paths[1]).IsEqualTo(Path.GetFullPath("/tmp/project-b"));
    }

    [Test]
    public async Task GetSortedPaths_ReturnOnlyPaths_NotFullEntries() {
        await Repos.AddAsync("/tmp/project-x");

        var paths = await Repos.GetSortedPathsAsync();

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

        await Repos.AddAsync(main);
        await Repos.AddAsync(wt);

        var entries = await Repos.LoadAsync();
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

        var entries = await Repos.LoadAsync();

        await Assert.That(entries.Length).IsEqualTo(2);
        // GetFullPath, like every assertion in this class: on Windows a rootless "/gone/repo"
        // normalizes to "<drive>:\gone\repo".
        var repo = entries.Single(e => e.Path == Path.GetFullPath("/gone/repo"));
        await Assert.That(repo.LastUsed).IsEqualTo(newer);
    }
}
