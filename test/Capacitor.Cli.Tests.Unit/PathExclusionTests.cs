namespace Capacitor.Cli.Tests.Unit;

public class PathExclusionTests {
    [Test]
    public async Task IsExcluded_returns_false_when_excludedPaths_is_null() {
        await Assert.That(PathExclusion.IsExcluded("/some/path", null)).IsFalse();
    }

    [Test]
    public async Task IsExcluded_returns_false_when_excludedPaths_is_empty() {
        await Assert.That(PathExclusion.IsExcluded("/some/path", [])).IsFalse();
    }

    [Test]
    public async Task IsExcluded_returns_false_when_cwd_is_null() {
        await Assert.That(PathExclusion.IsExcluded(null, ["/some/path"])).IsFalse();
    }

    [Test]
    public async Task IsExcluded_matches_exact_path() {
        using var tmp  = new TempDir();
        var       path = tmp.Path;

        await Assert.That(PathExclusion.IsExcluded(path, [path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_matches_descendant() {
        using var tmp = new TempDir();
        var       sub = Path.Combine(tmp.Path, "sub", "deeper");
        Directory.CreateDirectory(sub);

        await Assert.That(PathExclusion.IsExcluded(sub, [tmp.Path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_does_not_match_sibling_with_shared_prefix() {
        // /tmp/foo vs /tmp/foobar — must NOT match
        using var tmp    = new TempDir();
        var       foo    = Path.Combine(tmp.Path, "foo");
        var       foobar = Path.Combine(tmp.Path, "foobar");
        Directory.CreateDirectory(foo);
        Directory.CreateDirectory(foobar);

        await Assert.That(PathExclusion.IsExcluded(foobar, [foo])).IsFalse();
    }

    [Test]
    public async Task IsExcluded_ignores_trailing_separator_on_entry() {
        using var tmp = new TempDir();
        var       sub = Path.Combine(tmp.Path, "child");
        Directory.CreateDirectory(sub);

        await Assert.That(PathExclusion.IsExcluded(sub, [tmp.Path + Path.DirectorySeparatorChar])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_matches_descendant_whose_leaf_name_starts_with_dotdot() {
        // /ignored/..scratch is a legitimate child of /ignored. Path.GetRelativePath
        // returns "..scratch", which our containment check must not treat as a
        // parent-directory reference.
        using var tmp = new TempDir();
        var       sub = Path.Combine(tmp.Path, "..scratch");
        Directory.CreateDirectory(sub);

        await Assert.That(PathExclusion.IsExcluded(sub, [tmp.Path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_matches_deeper_descendant_under_dotdot_named_intermediate() {
        using var tmp = new TempDir();
        var       sub = Path.Combine(tmp.Path, "..data", "session");
        Directory.CreateDirectory(sub);

        await Assert.That(PathExclusion.IsExcluded(sub, [tmp.Path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_matches_any_entry() {
        using var tmp = new TempDir();
        var       sub = Path.Combine(tmp.Path, "child");
        Directory.CreateDirectory(sub);

        await Assert.That(PathExclusion.IsExcluded(sub, ["/nonexistent/path", tmp.Path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_resolves_symlinked_entry_against_real_cwd() {
        // User runs `kcap ignore /symlink-to-real` but session cwd reports
        // the resolved path. Both sides must normalize to the same target.
        using var real = new TempDir();
        using var link = TempSymlink.To(real.Path);

        // cwd uses the real path; entry uses the symlinked path.
        await Assert.That(PathExclusion.IsExcluded(real.Path, [link.Path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_resolves_parent_symlinks() {
        // /link -> /real, cwd is /link/sub. Ignoring /real (or /link) must match
        // /link/sub. Today this fails because only the leaf is resolved.
        using var real = new TempDir();
        using var link = TempSymlink.To(real.Path);

        var subUnderReal = Path.Combine(real.Path, "sub");
        Directory.CreateDirectory(subUnderReal);

        // The cwd reported by an agent that descended through the symlink path.
        var subUnderLink = Path.Combine(link.Path, "sub");

        await Assert.That(PathExclusion.IsExcluded(subUnderLink, [real.Path])).IsTrue();
        await Assert.That(PathExclusion.IsExcluded(subUnderLink, [link.Path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_resolves_symlinked_cwd_against_real_entry() {
        // Reverse direction: entry stored as real path, cwd reported via symlink.
        using var real = new TempDir();
        using var link = TempSymlink.To(real.Path);

        await Assert.That(PathExclusion.IsExcluded(link.Path, [real.Path])).IsTrue();
    }

    [Test]
    public async Task IsExcluded_ignores_null_entries() {
        using var tmp = new TempDir();

        await Assert.That(PathExclusion.IsExcluded(tmp.Path, [null!])).IsFalse();
    }

    [Test]
    public async Task IsExcluded_ignores_empty_entries() {
        using var tmp = new TempDir();

        await Assert.That(PathExclusion.IsExcluded(tmp.Path, [""])).IsFalse();
    }

    [Test]
    public async Task IsExcluded_ignores_whitespace_entries() {
        using var tmp = new TempDir();

        await Assert.That(PathExclusion.IsExcluded(tmp.Path, ["   "])).IsFalse();
    }

    [Test]
    public async Task IsExcluded_skips_bad_entries_but_still_matches_good_ones() {
        using var tmp = new TempDir();

        await Assert.That(PathExclusion.IsExcluded(tmp.Path, [null!, "", tmp.Path])).IsTrue();
    }

    [Test]
    public async Task Normalize_expands_tilde() {
        var home  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var normd = PathExclusion.Normalize("~");

        await Assert.That(normd).IsEqualTo(home.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Test]
    public async Task Normalize_expands_tilde_subpath() {
        var home     = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(home, "stuff");
        var normd    = PathExclusion.Normalize("~/stuff");

        await Assert.That(normd).IsEqualTo(expected);
    }

    [Test]
    public async Task Normalize_makes_relative_path_absolute() {
        var normd = PathExclusion.Normalize(".");

        await Assert.That(Path.IsPathRooted(normd)).IsTrue();
    }

    [Test]
    public async Task Normalize_strips_trailing_separator() {
        using var tmp       = new TempDir();
        var       withSlash = tmp.Path + Path.DirectorySeparatorChar;

        await Assert.That(PathExclusion.Normalize(withSlash))
            .DoesNotEndWith(Path.DirectorySeparatorChar.ToString());
    }
}

sealed class TempSymlink : IDisposable {
    public string Path { get; }

    TempSymlink(string path) => Path = path;

    public static TempSymlink To(string target) {
        var p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kap-pathex-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(p, target);

        return new(p);
    }

    public void Dispose() {
        try { Directory.Delete(Path); } catch {
            /* best effort */
        }
    }
}
