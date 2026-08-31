using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class PathExclusionTests {
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task IsExcluded_returns_false_when_excludedPaths_is_null() {
        await Assert.That(PathExclusion.IsExcluded("/some/path", null, Home)).IsFalse();
    }

    [Test]
    public async Task IsExcluded_returns_false_when_excludedPaths_is_empty() {
        await Assert.That(PathExclusion.IsExcluded("/some/path", [], Home)).IsFalse();
    }

    [Test]
    public async Task IsExcluded_returns_false_when_cwd_is_null() {
        await Assert.That(PathExclusion.IsExcluded(null, ["/some/path"], Home)).IsFalse();
    }

    [Test]
    public async Task IsExcluded_matches_exact_path() {
        using var tmp  = new TempDir();
        var       path = tmp.Path;

        await Assert.That(PathExclusion.IsExcluded(path, [path], Home)).IsTrue();
    }

    [Test]
    public async Task IsExcluded_matches_descendant() {
        using var tmp = new TempDir();
        var       sub = tmp.CreateDir("sub", "deeper");

        await Assert.That(PathExclusion.IsExcluded(sub, [tmp.Path], Home)).IsTrue();
    }

    [Test]
    public async Task IsExcluded_does_not_match_sibling_with_shared_prefix() {
        // foo vs foobar share a prefix but foobar is not a descendant — must NOT match.
        using var tmp    = new TempDir();
        var       foo    = tmp.PathTo("foo");
        var       foobar = tmp.PathTo("foobar");
        Directory.CreateDirectory(foo);
        Directory.CreateDirectory(foobar);

        await Assert.That(PathExclusion.IsExcluded(foobar, [foo], Home)).IsFalse();
    }

    [Test]
    public async Task IsExcluded_ignores_trailing_separator_on_entry() {
        using var tmp = new TempDir();
        var       sub = tmp.CreateDir("child");

        await Assert.That(PathExclusion.IsExcluded(sub, [tmp.Path + Path.DirectorySeparatorChar], Home)).IsTrue();
    }

    [Test]
    public async Task IsExcluded_matches_descendant_whose_leaf_name_starts_with_dotdot() {
        // /ignored/..scratch is a legitimate child of /ignored. Path.GetRelativePath
        // returns "..scratch", which our containment check must not treat as a
        // parent-directory reference.
        using var tmp = new TempDir();
        var       sub = tmp.CreateDir("..scratch");

        await Assert.That(PathExclusion.IsExcluded(sub, [tmp.Path], Home)).IsTrue();
    }

    [Test]
    public async Task IsExcluded_matches_deeper_descendant_under_dotdot_named_intermediate() {
        using var tmp = new TempDir();
        var       sub = tmp.CreateDir("..data", "session");

        await Assert.That(PathExclusion.IsExcluded(sub, [tmp.Path], Home)).IsTrue();
    }

    [Test]
    public async Task IsExcluded_matches_any_entry() {
        using var tmp = new TempDir();
        var       sub = tmp.CreateDir("child");

        await Assert.That(PathExclusion.IsExcluded(sub, ["/nonexistent/path", tmp.Path], Home)).IsTrue();
    }

    [Test]
    public async Task IsExcluded_resolves_symlinked_entry_against_real_cwd() {
        // User runs `kcap ignore /symlink-to-real` but session cwd reports
        // the resolved path. Both sides must normalize to the same target.
        using var real = new TempDir();
        using var link = TempSymlink.To(real.Path);

        await Assert.That(PathExclusion.IsExcluded(real.Path, [link.Path], Home)).IsTrue();
    }

    [Test]
    public async Task IsExcluded_resolves_parent_symlinks() {
        // link -> real, cwd is link/sub: ignoring real (or link) must match link/sub too,
        // which requires resolving symlinks in parent components, not just the leaf.
        using var real = new TempDir();
        using var link = TempSymlink.To(real.Path);

        var subUnderReal = real.CreateDir("sub");

        // The cwd reported by an agent that descended through the symlink path.
        var subUnderLink = Path.Combine(link.Path, "sub");

        await Assert.That(PathExclusion.IsExcluded(subUnderLink, [real.Path], Home)).IsTrue();
        await Assert.That(PathExclusion.IsExcluded(subUnderLink, [link.Path], Home)).IsTrue();
    }

    [Test]
    public async Task IsExcluded_resolves_symlinked_cwd_against_real_entry() {
        // Reverse direction: entry stored as real path, cwd reported via symlink.
        using var real = new TempDir();
        using var link = TempSymlink.To(real.Path);

        await Assert.That(PathExclusion.IsExcluded(link.Path, [real.Path], Home)).IsTrue();
    }

    [Test]
    public async Task IsExcluded_ignores_null_entries() {
        using var tmp = new TempDir();

        await Assert.That(PathExclusion.IsExcluded(tmp.Path, [null!], Home)).IsFalse();
    }

    [Test]
    public async Task IsExcluded_ignores_empty_entries() {
        using var tmp = new TempDir();

        await Assert.That(PathExclusion.IsExcluded(tmp.Path, [""], Home)).IsFalse();
    }

    [Test]
    public async Task IsExcluded_ignores_whitespace_entries() {
        using var tmp = new TempDir();

        await Assert.That(PathExclusion.IsExcluded(tmp.Path, ["   "], Home)).IsFalse();
    }

    [Test]
    public async Task IsExcluded_skips_bad_entries_but_still_matches_good_ones() {
        using var tmp = new TempDir();

        await Assert.That(PathExclusion.IsExcluded(tmp.Path, [null!, "", tmp.Path], Home)).IsTrue();
    }

    // A home that needs no expanding, since Normalize runs GetFullPath over it: a Unix-shaped
    // literal picks up the current drive on Windows. Non-existent, so no symlink resolution either.
    static UserHome FakeHome => new(Path.GetFullPath("/fake/home"));

    [Test]
    public async Task Normalize_expands_tilde() {
        var home = FakeHome;

        await Assert.That(PathExclusion.Normalize("~", home)).IsEqualTo(home.Path);
    }

    [Test]
    public async Task Normalize_expands_tilde_subpath() {
        var home = FakeHome;

        await Assert.That(PathExclusion.Normalize("~/stuff", home))
                    .IsEqualTo(Path.Combine(home.Path, "stuff"));
    }

    [Test]
    public async Task Normalize_makes_relative_path_absolute() {
        var normd = PathExclusion.Normalize(".", Home);

        await Assert.That(Path.IsPathRooted(normd)).IsTrue();
    }

    [Test]
    public async Task Normalize_strips_trailing_separator() {
        using var tmp       = new TempDir();
        var       withSlash = tmp.Path + Path.DirectorySeparatorChar;

        await Assert.That(PathExclusion.Normalize(withSlash, Home))
            .DoesNotEndWith(Path.DirectorySeparatorChar.ToString());
    }
}

sealed class TempSymlink : IDisposable {
    readonly TempDir _parent;

    public string Path { get; }

    TempSymlink(TempDir parent, string path) {
        _parent = parent;
        Path    = path;
    }

    public static TempSymlink To(string target) {
        var parent = new TempDir("pathexlink");
        var link   = parent.PathTo("link");

        Directory.CreateSymbolicLink(link, target);

        return new(parent, link);
    }

    // The link first: deleting the directory tree would follow it into the target otherwise.
    public void Dispose() {
        try { Directory.Delete(Path); } catch { /* best effort */ }
        _parent.Dispose();
    }
}
