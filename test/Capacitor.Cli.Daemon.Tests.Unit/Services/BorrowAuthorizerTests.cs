using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Behaviour tests for <see cref="BorrowAuthorizer"/> — the daemon-side gate deciding whether a
/// cwd may be borrowed (a read-only reviewer run in it). Uses real temp dirs, real symlinks, and
/// real <c>git init</c> repos so the canonicalization + git-root + allowlist policy is exercised
/// against the actual filesystem rather than mocks.
/// </summary>
public class BorrowAuthorizerTests {
    [Test]
    public async Task Absent_path_is_not_allowed_with_path_absent_reason() {
        using var missingDir = TempDir.WithPathTo("kcap-borrow-missing", out var missing);

        var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(missing);

        await Assert.That(result.Allowed).IsFalse();
        await Assert.That(result.Reason).IsEqualTo("path_absent");
        await Assert.That(result.CanonicalCwd).IsNull();
        await Assert.That(result.CanonicalGitRoot).IsNull();
    }

    [Test]
    public async Task GitRooted_cwd_with_empty_allowlist_is_allowed() {
        using var repo = MakeTempRepo();
        var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(repo.Path);

        await Assert.That(result.Allowed).IsTrue();
        await Assert.That(result.CanonicalGitRoot).IsEqualTo(BorrowAuthorizer.Canonicalize(repo.Path));
    }

    [Test]
    public async Task NonRepo_cwd_with_empty_allowlist_is_not_allowed() {
        using var tmp = new TempDir();
        var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(tmp.Path);

        await Assert.That(result.Allowed).IsFalse();
        await Assert.That(result.Reason).IsEqualTo("not_allowed");
        await Assert.That(result.CanonicalGitRoot).IsNull();
    }

    [Test]
    public async Task NonRepo_cwd_matching_explicit_allowlist_is_allowed() {
        using var tmp = new TempDir();
        var canonical  = BorrowAuthorizer.Canonicalize(tmp.Path);
        var authorizer = new BorrowAuthorizer(new DaemonConfig { AllowedRepoPaths = [canonical] });

        var result = await authorizer.AuthorizeBorrowAsync(tmp.Path);

        await Assert.That(result.Allowed).IsTrue();
        await Assert.That(result.Reason).IsNull();
    }

    [Test]
    public async Task Symlinked_cwd_resolving_into_allowed_git_root_is_allowed() {
        using var repo = MakeTempRepo();
        using var linkParent = new TempDir();
        var link       = linkParent.PathTo("link-to-repo");
        Directory.CreateSymbolicLink(link, repo.Path);

        var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(link);

        await Assert.That(result.Allowed).IsTrue();
        await Assert.That(result.CanonicalCwd).IsEqualTo(BorrowAuthorizer.Canonicalize(repo.Path));
    }

    [Test]
    public async Task Symlinked_cwd_escaping_nonempty_allowlist_is_not_allowed() {
        using var tmp = new TempDir();
        var allowedRoot = tmp.CreateDir("allowed");
        var outsideRoot = tmp.CreateDir("outside");
        var link        = allowedRoot.PathTo("escape-link");

        Directory.CreateSymbolicLink(link, outsideRoot);

        var authorizer = new BorrowAuthorizer(
            new DaemonConfig { AllowedRepoPaths = [BorrowAuthorizer.Canonicalize(allowedRoot)] }
        );

        var result = await authorizer.AuthorizeBorrowAsync(link);

        await Assert.That(result.Allowed).IsFalse();
        await Assert.That(result.Reason).IsEqualTo("not_allowed");
    }

    [Test]
    public async Task Ancestor_symlink_escaping_nonempty_allowlist_is_not_allowed() {
        // Allowlisted tree: allowedRoot/*. Inside it, an ANCESTOR dir is a symlink pointing OUT of
        // the tree; the leaf (cwd) is a real, non-symlink dir under that ancestor. A leaf-only
        // canonicalization would leave the path textually under allowedRoot and wrongly allow it.
        using var tmp = new TempDir();
        var allowedRoot  = tmp.CreateDir("allowed");
        var outsideRoot  = tmp.CreateDir("outside");
        var realLeaf     = outsideRoot.PathTo("x");
        var linkAncestor = allowedRoot.PathTo("linkdir");

        Directory.CreateDirectory(realLeaf);
        Directory.CreateSymbolicLink(linkAncestor, outsideRoot);

        var cwd = Path.Combine(linkAncestor, "x"); // allowedRoot/linkdir/x → outsideRoot/x

        var authorizer = new BorrowAuthorizer(
            new DaemonConfig { AllowedRepoPaths = [BorrowAuthorizer.Canonicalize(allowedRoot) + "/*"] }
        );

        var result = await authorizer.AuthorizeBorrowAsync(cwd);

        await Assert.That(result.Allowed).IsFalse();
        await Assert.That(result.Reason).IsEqualTo("not_allowed");
        await Assert.That(result.CanonicalCwd).IsEqualTo(BorrowAuthorizer.Canonicalize(realLeaf));
    }

    [Test]
    public async Task Ancestor_symlink_resolving_into_allowed_root_is_allowed() {
        // The reverse: an ancestor symlink that resolves INTO the allowlisted tree is allowed, and
        // CanonicalCwd is the resolved real path (not the link path).
        using var tmp = new TempDir();
        var allowedRoot  = tmp.CreateDir("allowed");
        var linkParent   = tmp.CreateDir("link");
        var realLeaf     = allowedRoot.PathTo("proj", "x");
        var linkAncestor = linkParent.PathTo("to-allowed");

        Directory.CreateDirectory(realLeaf);
        Directory.CreateSymbolicLink(linkAncestor, allowedRoot);

        var cwd = Path.Combine(linkAncestor, "proj", "x"); // linkParent/to-allowed/proj/x → allowedRoot/proj/x

        var authorizer = new BorrowAuthorizer(
            new DaemonConfig { AllowedRepoPaths = [BorrowAuthorizer.Canonicalize(allowedRoot) + "/*"] }
        );

        var result = await authorizer.AuthorizeBorrowAsync(cwd);

        await Assert.That(result.Allowed).IsTrue();
        await Assert.That(result.CanonicalCwd).IsEqualTo(BorrowAuthorizer.Canonicalize(realLeaf));
    }

    static TempDir MakeTempRepo() {
        var repo = new TempDir();
        GitRepo.At(repo.Path).Do("init", "-q");

        return repo;
    }

}
